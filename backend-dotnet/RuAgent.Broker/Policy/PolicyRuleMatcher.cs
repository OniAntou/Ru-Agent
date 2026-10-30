using System.Globalization;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Tools;

namespace RuAgent.Broker.Policy;

/// <summary>
/// A single matcher pattern compiled from wire text. Structural validity is enforced while
/// the snapshot is bound — a malformed value throws before a lease can carry the snapshot —
/// so every matcher the evaluator sees is matchable.
/// </summary>
public sealed record MatcherSpec(
    string? ToolId,
    string? ToolVersion,
    string? CanonicalPathPattern,
    string? ResolvedExecutable,
    IReadOnlyList<string> ArgvPattern,
    string? CwdPattern,
    string? OperationClass,
    string? NetworkIntent);

public sealed record PolicyRuleSpec(
    string RuleId,
    string Scope,
    string Decision,
    MatcherSpec Matcher,
    long CreationOrder,
    DateTimeOffset? ExpiresAt);

public static class PolicyRuleMatcher
{
    private enum ArgvMatchSemantics
    {
        Prefix,
        Exact,
    }

    /// <summary>
    /// Parses a wire rule into a spec. Structural validation already happened at snapshot
    /// validation; this compiles matcher patterns and throws on any malformed value.
    /// </summary>
    public static PolicyRuleSpec Parse(BrokerPolicyRuleWire rule) =>
        new(
            rule.RuleId,
            rule.Scope,
            rule.Decision,
            CompileMatcher(rule.Matcher),
            rule.CreationOrder,
            ParseTimestampOrNull(rule.ExpiresAt));

    public static bool Matches(PolicyRuleSpec rule, PolicyContext context, DateTimeOffset now)
    {
        if (rule.ExpiresAt is { } expiresAt && now >= expiresAt)
        {
            return false;
        }

        // An allow for a process-identity call must name its executable and argv; without them it would
        // authorize every call of that tool. Deny and ask rules keep their wildcard semantics.
        if (rule.Decision == "allow" && ApprovalRememberPolicy.AllowRuleLacksProcessIdentity(rule.Matcher, context))
        {
            return false;
        }

        return MatchesMatcher(rule.Matcher, context, isAllowRule: rule.Decision == "allow",
            includeRequestedExecutable: rule.Decision != "allow");
    }

    public static bool MatchesMatcher(MatcherSpec matcher, PolicyContext context)
        => MatchesMatcher(matcher, context, isAllowRule: false);

    private static bool MatchesMatcher(MatcherSpec matcher, PolicyContext context, bool isAllowRule,
        bool includeRequestedExecutable = false)
    {
        if (matcher.ToolId is not null && !ToolIdMatches(matcher.ToolId, context))
        {
            return false;
        }

        if (matcher.ToolVersion is not null && !string.Equals(matcher.ToolVersion, context.ToolVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.OperationClass is not null && !string.Equals(matcher.OperationClass, context.OperationClass, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.NetworkIntent is not null && !string.Equals(matcher.NetworkIntent, context.NetworkIntent, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.CanonicalPathPattern is not null)
        {
            var candidate = PatternCandidate(context);
            if (candidate is null || !GlobMatch(matcher.CanonicalPathPattern, candidate))
            {
                return false;
            }
        }

        // Existing bare-name rules retain their original request-label semantics.
        // New remembered approvals carry the canonical full path and match that identity.
        if (matcher.ResolvedExecutable is { } executable)
        {
            var matches = ExecutableMatches(executable, Path.IsPathFullyQualified(executable)
                ? context.ResolvedExecutable : context.RequestedExecutable ?? context.ResolvedExecutable);
            // Canonicalization must not let an alias bypass an existing deny/ask rule.
            // Allow rules continue to require the selected identity above.
            if (!matches && includeRequestedExecutable)
                matches = ExecutableMatches(executable, context.RequestedExecutable)
                    || ExecutableMatches(executable, context.ResolvedExecutable);
            if (!matches) return false;
        }

        // Allow rules derived from terminal calls distinguish reviewed exact
        // invocations from normal command prefixes. Restrictions intentionally
        // keep glob/prefix matching so a broad deny or ask still wins.
        if (matcher.ArgvPattern.Count > 0
            && !ArgvMatches(
                matcher.ArgvPattern,
                context.Argv,
                GetArgvMatchSemantics(isAllowRule, matcher, context)))
        {
            return false;
        }

        if (matcher.CwdPattern is not null
            && (context.CanonicalCwd is null || !GlobMatch(matcher.CwdPattern, context.CanonicalCwd)))
        {
            return false;
        }

        return true;
    }

    internal static bool GlobMatch(string pattern, string candidate)
    {
        // Restricted grammar: '*' matches within one path segment, '**' spans separators,
        // everything else compares case-insensitively as literals. Patterns are anchored
        // to the whole value, separators compare uniformly, and "{dir}/**" also covers
        // the directory itself so a directory grant reaches its own listing.
        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedCandidate = candidate.Replace('\\', '/');
        if (normalizedPattern.EndsWith("/**", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                normalizedPattern[..^3],
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GlobMatchCore(normalizedPattern.AsSpan(), normalizedCandidate.AsSpan());
    }

    private static bool GlobMatchCore(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate)
    {
        while (true)
        {
            if (pattern.IsEmpty)
            {
                return candidate.IsEmpty;
            }

            if (pattern.Length >= 2 && pattern[0] == '*' && pattern[1] == '*')
            {
                var rest = pattern[2..];
                for (var i = 0; i <= candidate.Length; i++)
                {
                    if (GlobMatchCore(rest, candidate[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pattern[0] == '*')
            {
                var rest = pattern[1..];
                for (var i = 0; i <= candidate.Length; i++)
                {
                    if (i > 0 && IsSeparator(candidate[i - 1]))
                    {
                        break;
                    }

                    if (GlobMatchCore(rest, candidate[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (candidate.IsEmpty || !char.ToLowerInvariant(pattern[0]).Equals(char.ToLowerInvariant(candidate[0])))
            {
                return false;
            }

            pattern = pattern[1..];
            candidate = candidate[1..];
        }
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    internal static string? PatternCandidate(PolicyContext context)
    {
        if (context.RelativePathInsideWorkspace is not null)
        {
            return context.RelativePathInsideWorkspace;
        }

        return context.Target?.CanonicalPath;
    }

    private static bool ToolIdMatches(string matcherId, PolicyContext context)
    {
        if (matcherId == context.ToolId || AgentToolPolicyAliases.Matches(matcherId, context.ToolId, context.ToolOp, context.BashMode))
            return true;
        var adapterId = CodingToolArguments.AdapterToolId(context.ToolId);
        if (context.ToolId == "apply_patch" && context.OperationClass != PolicyOperationClasses.Patch)
            adapterId = context.OperationClass == PolicyOperationClasses.Edit ? "edit" : "write";
        return adapterId is not null && (matcherId == adapterId
            || AgentToolPolicyAliases.Matches(matcherId, adapterId, context.ToolOp, context.BashMode));
    }

    private static bool ExecutableMatches(string pattern, string? resolvedExecutable)
    {
        if (resolvedExecutable is null)
        {
            return false;
        }

        if (string.Equals(pattern, resolvedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var baseName = Path.GetFileName(resolvedExecutable);
        return string.Equals(pattern, baseName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when an allow of this matcher compares argv exactly, with no glob and no prefix.</summary>
    internal static bool UsesExactArgv(MatcherSpec matcher, PolicyContext context) =>
        matcher.ArgvPattern.Count > 0
        && GetArgvMatchSemantics(isAllowRule: true, matcher, context) == ArgvMatchSemantics.Exact;

    private static ArgvMatchSemantics GetArgvMatchSemantics(
        bool isAllowRule,
        MatcherSpec matcher,
        PolicyContext context)
    {
        // An app list is a grant, not a command prefix: a remembered [notepad.exe]
        // must never cover a later request that adds another app (ADR 0031).
        if (isAllowRule && string.Equals(context.ToolId, "computer", StringComparison.Ordinal))
        {
            return ArgvMatchSemantics.Exact;
        }

        if (isAllowRule && context.OperationClass == PolicyOperationClasses.Terminal)
        {
            if (context.BashMode == "shell"
                || CommandIntentResolver.IsPowerShellCommandInvocation(context.ResolvedExecutable, context.Argv))
            {
                return ArgvMatchSemantics.Exact;
            }

            // Flag-first native invocations include PowerShell wrappers. This does
            // not depend on the resolver's current shell startup flag sequence.
            if (IsLeadingFlag(matcher.ArgvPattern[0]))
            {
                return ArgvMatchSemantics.Exact;
            }
        }

        return ArgvMatchSemantics.Prefix;
    }

    private static bool IsLeadingFlag(string argument) =>
        argument.StartsWith("-", StringComparison.Ordinal)
        || argument.StartsWith("/", StringComparison.Ordinal);

    private static bool ArgvMatches(
        IReadOnlyList<string> pattern,
        IReadOnlyList<string> argv,
        ArgvMatchSemantics semantics)
    {
        if (semantics == ArgvMatchSemantics.Exact)
        {
            return pattern.SequenceEqual(argv, StringComparer.Ordinal);
        }

        if (pattern.Count > argv.Count)
        {
            return false;
        }

        for (var i = 0; i < pattern.Count; i++)
        {
            if (!GlobMatch(pattern[i], argv[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static MatcherSpec CompileMatcher(BrokerPolicyMatcherWire matcher)
    {
        RequireWellFormed(matcher.ToolId, 128);
        RequireWellFormed(matcher.ToolVersion, 32);
        RequireWellFormed(matcher.CanonicalPathPattern, 4096);
        RequireWellFormed(matcher.ResolvedExecutable, TerminalLimits.MaxCanonicalExecutableChars);
        RequireWellFormed(matcher.CwdPattern, 4096);

        if (matcher.ArgvPattern is { } argv)
        {
            if (argv.Count > 64)
            {
                throw new AgentProtocolException("invalid_snapshot_rule");
            }

            foreach (var element in argv)
            {
                RequireWellFormed(element, 4096);
            }
        }

        return new MatcherSpec(
            matcher.ToolId,
            matcher.ToolVersion,
            matcher.CanonicalPathPattern,
            matcher.ResolvedExecutable,
            matcher.ArgvPattern ?? Array.Empty<string>(),
            matcher.CwdPattern,
            matcher.OperationClass,
            matcher.NetworkIntent);
    }

    private static void RequireWellFormed(string? value, int maximumLength)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength))
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }
    }

    private static DateTimeOffset? ParseTimestampOrNull(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }
}
