using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Policy;

/// <summary>The target identity a tool binds when its approval is remembered.</summary>
public enum ApprovalIdentityClass
{
    /// <summary>Not in the allowlist: never remembered.</summary>
    Unknown = 0,

    /// <summary>In the allowlist with no target identity to bind: never remembered.</summary>
    NotRememberable,

    /// <summary>A workspace or external file path, bound with the tool id and operation class.</summary>
    FileTarget,

    /// <summary>A terminal launch, bound with its resolved executable, argv and canonical cwd.</summary>
    ProcessLaunch,

    /// <summary>An MCP server call or tool listing, bound with its server executable and argv.</summary>
    McpCall,

    /// <summary>A computer-use app grant (request_access or focus), bound with its app list.</summary>
    ComputerGrant,

    /// <summary>A configured language server launch, bound with its executable, argv, cwd and file path.</summary>
    LanguageServer,
}

/// <summary>
/// Builds the remembered-approval plan for one approved call from the Broker PolicyContext the evaluator
/// matched. Rememberability is an explicit allowlist keyed by the advertised tool id; a tool that is not
/// listed, or a call whose identity does not bind, is not remembered.
/// </summary>
/// <remarks>
/// Apart from <see cref="ExternalDirectoryPattern"/>, which reads the filesystem to find the nearest existing
/// folder, <see cref="Build"/> is a pure function of its arguments.
/// </remarks>
public static class ApprovalRememberPolicy
{
    private const string ExternalDirectoryReason = "external_directory_required";
    private const string TerminalProtectedPathReason = "terminal_protected_path_requires_approval";
    private const string RuleAsksReason = "rule_asks";
    private const string ProbeRuleId = "approval-probe";
    private const int MaxRememberedArgvTokens = 2;

    // Limits of the Host's PolicyMatcher. A matcher beyond them cannot be converted into a stored rule,
    // so the plan refuses it here rather than failing later in the Host.
    private const int MaxToolIdLength = 128;
    private const int MaxToolVersionLength = 32;
    private const int MaxPathLength = 4096;
    private const int MaxExecutableLength = 256;
    private const int MaxCwdLength = 4096;
    private const int MaxOperationClassLength = 64;
    private const int MaxNetworkIntentLength = 64;
    private const int MaxArgvCount = 64;
    private const int MaxArgvElementLength = 4096;

    /// <summary>
    /// The matchers an approval of <paramref name="context"/> may remember.
    /// </summary>
    /// <param name="context">The Broker context the evaluator matched and the approval granted.</param>
    /// <param name="toolId">The advertised tool id; must equal <see cref="PolicyContext.ToolId"/>.</param>
    /// <param name="toolVersion">The tool version; must equal <see cref="PolicyContext.ToolVersion"/>.</param>
    /// <param name="askSafeCode">The reason code of the ask that required the approval, if any.</param>
    /// <param name="externalDirectoryRequested">True when the ask was an outside-workspace folder request.</param>
    public static ApprovalRememberPlan Build(
        PolicyContext context,
        string toolId,
        string toolVersion,
        string? askSafeCode,
        bool externalDirectoryRequested)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(toolId, context.ToolId, StringComparison.Ordinal)
            || !string.Equals(toolVersion, context.ToolVersion, StringComparison.Ordinal))
        {
            return ApprovalRememberPlan.None;
        }

        var identity = Classify(toolId);
        if (!IsRememberableCall(identity, context))
        {
            return ApprovalRememberPlan.None;
        }

        if (externalDirectoryRequested)
        {
            return new ApprovalRememberPlan(
                SessionMatcher: null,
                DurableMatcher: ExternalDirectoryMatcher(identity, context));
        }

        var binding = BindingFor(identity);
        var session = MatcherFor(context, binding, context.Argv);

        // Explicit asks and protected-path asks outrank any remembered allow, so they never become a
        // project-wide rule. The exact session intent is still remembered, as before.
        if (askSafeCode is TerminalProtectedPathReason or RuleAsksReason)
        {
            return new ApprovalRememberPlan(session, DurableMatcher: null);
        }

        var durableArgv = identity == ApprovalIdentityClass.ProcessLaunch ? GeneralizedArgv(context) : context.Argv;
        return new ApprovalRememberPlan(session, MatcherFor(context, binding, durableArgv));
    }

    /// <summary>
    /// The allowlist: every descriptor tool id maps to an explicit class, and an unlisted id is
    /// <see cref="ApprovalIdentityClass.Unknown"/>.
    /// </summary>
    internal static ApprovalIdentityClass Classify(string toolId) => toolId switch
    {
        "read" or "list" or "grep" or "glob"
            or "read_file" or "write_file" or "edit_file" or "file_delete" => ApprovalIdentityClass.FileTarget,
        "run_process" or "powershell" => ApprovalIdentityClass.ProcessLaunch,
        "mcp" => ApprovalIdentityClass.McpCall,
        "computer" => ApprovalIdentityClass.ComputerGrant,
        "code_intelligence" => ApprovalIdentityClass.LanguageServer,
        "apply_patch" or "file_restore" or "run_code" or "terminal_read" or "terminal_stop"
            or "help" or "todo_write" or "web_search" or "web_fetch" or "skill" or "ask_user"
            or "agent_spawn" or "agent_send" or "agent_wait" or "agent_cancel" or "agent_apply"
            => ApprovalIdentityClass.NotRememberable,
        _ => ApprovalIdentityClass.Unknown,
    };

    /// <summary>
    /// Calls whose allow rules must name an executable and an argv. The Host saves these tools' rules
    /// without a provenance the evaluator can check, so an identity-less allow would authorize every call of
    /// the tool. Terminal allows are not guarded: a cwd-only terminal rule is an explicit user decision, and
    /// the Host's remembered terminal rules always carry the full identity anyway.
    /// </summary>
    internal static bool RequiresProcessIdentity(PolicyContext context) =>
        string.Equals(context.ToolId, "code_intelligence", StringComparison.Ordinal)
        || (string.Equals(context.ToolId, "mcp", StringComparison.Ordinal) && context.ToolOp is "call" or "list_tools")
        || (string.Equals(context.ToolId, "computer", StringComparison.Ordinal) && context.ToolOp is "request_access" or "focus");

    /// <summary>True when an allow rule is missing the identity that <see cref="RequiresProcessIdentity"/> needs.</summary>
    internal static bool AllowRuleLacksProcessIdentity(MatcherSpec matcher, PolicyContext context) =>
        RequiresProcessIdentity(context)
        && (matcher.ResolvedExecutable is null || matcher.ArgvPattern.Count == 0);

    /// <summary>
    /// True when the approved call's tool class may be remembered at all. A remembered pattern can still be
    /// refused for a specific target (a drive root, for one); that degrades the approval to one-time only.
    /// </summary>
    public static bool IsRememberableTool(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsRememberableCall(Classify(context.ToolId), context);
    }

    private static bool IsRememberableCall(ApprovalIdentityClass identity, PolicyContext context) => identity switch
    {
        ApprovalIdentityClass.FileTarget or ApprovalIdentityClass.LanguageServer => true,
        ApprovalIdentityClass.ProcessLaunch => string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal),
        ApprovalIdentityClass.McpCall => context.ToolOp is "call" or "list_tools",
        ApprovalIdentityClass.ComputerGrant => context.ToolOp is "request_access" or "focus",
        _ => false,
    };

    private readonly record struct IdentityBinding(bool Path, bool Executable, bool Argv, bool Cwd);

    private static IdentityBinding BindingFor(ApprovalIdentityClass identity) => identity switch
    {
        ApprovalIdentityClass.FileTarget => new(Path: true, Executable: false, Argv: false, Cwd: false),
        ApprovalIdentityClass.ProcessLaunch => new(Path: false, Executable: true, Argv: true, Cwd: true),
        ApprovalIdentityClass.McpCall or ApprovalIdentityClass.ComputerGrant
            => new(Path: false, Executable: true, Argv: true, Cwd: false),
        ApprovalIdentityClass.LanguageServer => new(Path: true, Executable: true, Argv: true, Cwd: true),
        _ => throw new ArgumentOutOfRangeException(nameof(identity), identity, "not a rememberable identity class"),
    };

    private static BrokerPolicyMatcherWire? MatcherFor(
        PolicyContext context,
        IdentityBinding binding,
        IReadOnlyList<string> argv)
    {
        // The path candidate is the same one PolicyRuleMatcher compares: the workspace-relative path when
        // inside, otherwise the canonical path.
        var path = binding.Path ? PolicyRuleMatcher.PatternCandidate(context) : null;
        var executable = binding.Executable ? context.ResolvedExecutable : null;
        var cwd = binding.Cwd ? context.CanonicalCwd : null;
        var argvPattern = binding.Argv ? argv.ToArray() : null;
        if ((binding.Path && (path is null || ContainsWildcard(path)))
            || (binding.Executable && executable is null)
            || (binding.Cwd && cwd is null)
            || (binding.Argv && argvPattern is not { Length: > 0 }))
        {
            return null;
        }

        return Validated(
            new BrokerPolicyMatcherWire(
                context.ToolId,
                context.ToolVersion,
                path,
                executable,
                argvPattern,
                cwd,
                context.OperationClass,
                context.NetworkIntent),
            context);
    }

    private static BrokerPolicyMatcherWire? ExternalDirectoryMatcher(ApprovalIdentityClass identity, PolicyContext context)
    {
        // A folder grant is path-only and so is honored for any tool on that path; only a file target
        // outside the workspace earns one. A workspace target would otherwise grant the whole workspace.
        var target = context.Target;
        if (identity != ApprovalIdentityClass.FileTarget || target is null || !target.IsOutsideWorkspace)
        {
            return null;
        }

        // The "\**" subtree suffix is the only glob the folder grant may carry: a literal '*' in the path
        // itself would widen it beyond the folder.
        var pattern = ContainsWildcard(target.CanonicalPath) ? null : ExternalDirectoryPattern(target.CanonicalPath);
        return pattern is null
            ? null
            : Validated(new BrokerPolicyMatcherWire(null, null, pattern, null, null, null, null, null), context);
    }

    /// <summary>
    /// The nearest existing folder of an approved path as a subtree pattern. The candidate is already the
    /// canonical path the evaluator compares, so its existing ancestors are canonical prefixes of it and the
    /// pattern matches that candidate. A root folder (drive, volume or share) yields no pattern.
    /// </summary>
    internal static string? ExternalDirectoryPattern(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxPathLength)
        {
            return null;
        }

        try
        {
            string? current = candidate;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            var folder = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // A root folder's subtree is the whole volume, so no folder pattern may be derived from it.
            if (IsRootFolder(current))
            {
                return null;
            }

            // The "\**" suffix must fit within the path cap.
            return folder.Length + 3 > MaxPathLength ? null : folder + "\\**";
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="folder"/> is a drive, volume or share root ("D:\", "\\server\share\",
    /// "\\?\Volume{...}\"), or names nothing at all. Such a folder is never a grant or remembered pattern.
    /// </summary>
    internal static bool IsRootFolder(string folder)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var trimmed = folder.TrimEnd(separators);
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return true;
        }

        var root = Path.GetPathRoot(folder);
        return root is null
            || string.Equals(trimmed, root.TrimEnd(separators), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when an approval's granted directory boundary is a root folder that may not become a session grant.</summary>
    internal static bool IsRootBoundary(string? boundary) => boundary is not null && IsRootFolder(boundary);

    /// <summary>
    /// A terminal run remembers at most its first two leading non-flag argv tokens, so a command prefix
    /// such as "git status" or "npm run build" covers its subcommand. A shell wrapper stays exact.
    /// </summary>
    private static IReadOnlyList<string> GeneralizedArgv(PolicyContext context)
    {
        if (IsShellInvocation(context))
        {
            return context.Argv;
        }

        var take = 0;
        while (take < context.Argv.Count
            && take < MaxRememberedArgvTokens
            && !IsLeadingFlag(context.Argv[take]))
        {
            take++;
        }

        return take == 0 ? context.Argv : context.Argv.Take(take).ToArray();
    }

    private static bool IsShellInvocation(PolicyContext context) =>
        string.Equals(context.BashMode, "shell", StringComparison.Ordinal)
        || CommandIntentResolver.IsPowerShellCommandInvocation(context.ResolvedExecutable, context.Argv);

    private static bool IsLeadingFlag(string argument) =>
        argument.StartsWith("-", StringComparison.Ordinal) || argument.StartsWith("/", StringComparison.Ordinal);

    /// <summary>
    /// Accepts a matcher only when the Broker parses it and the Host limits hold. A literal '*' in a field
    /// the evaluator globs would widen the approval, so such a matcher is refused.
    /// </summary>
    private static BrokerPolicyMatcherWire? Validated(BrokerPolicyMatcherWire matcher, PolicyContext context)
    {
        if (!WithinHostLimits(matcher))
        {
            return null;
        }

        PolicyRuleSpec rule;
        try
        {
            rule = PolicyRuleMatcher.Parse(new BrokerPolicyRuleWire(ProbeRuleId, "project", "allow", matcher, 0, null));
        }
        catch (AgentProtocolException)
        {
            return null;
        }

        if (ContainsWildcard(matcher.CwdPattern))
        {
            return null;
        }

        if (rule.Matcher.ArgvPattern.Any(token => token.Contains('*'))
            && !PolicyRuleMatcher.UsesExactArgv(rule.Matcher, context))
        {
            return null;
        }

        return matcher;
    }

    private static bool WithinHostLimits(BrokerPolicyMatcherWire matcher) =>
        Bounded(matcher.ToolId, MaxToolIdLength)
        && Bounded(matcher.ToolVersion, MaxToolVersionLength)
        && Bounded(matcher.CanonicalPathPattern, MaxPathLength)
        && Bounded(matcher.ResolvedExecutable, MaxExecutableLength)
        && Bounded(matcher.CwdPattern, MaxCwdLength)
        && Bounded(matcher.OperationClass, MaxOperationClassLength)
        && Bounded(matcher.NetworkIntent, MaxNetworkIntentLength)
        && (matcher.ArgvPattern is null
            || (matcher.ArgvPattern.Count <= MaxArgvCount
                && matcher.ArgvPattern.All(token => token is not null && Bounded(token, MaxArgvElementLength))));

    private static bool Bounded(string? value, int maximumLength) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Contains('\0'));

    private static bool ContainsWildcard(string? value) => value is not null && value.Contains('*');
}
