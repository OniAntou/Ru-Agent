using System.Globalization;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Policy;

/// <summary>
/// The Broker-bound immutable snapshot: wire rules parsed into matchable specs. Parsing is
/// total — any structurally invalid rule fails closed before a lease can carry the snapshot.
/// </summary>
public sealed record BoundPolicySnapshot(
    long Version,
    string Preset,
    string WorkspaceId,
    string VolumeSerialNumber,
    string RootFileId,
    string DisplayIdentityHash,
    IReadOnlyList<PolicyRuleSpec> OrderedRules,
    DateTimeOffset CreatedAt,
    string Sha256)
{
    public bool HasInvalidMatcher => OrderedRules.Any(rule => !rule.IsValid);

    public static BoundPolicySnapshot Parse(BrokerPolicySnapshotWire snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BrokerPolicyContracts.ValidateSnapshot(snapshot);
        var rules = new List<PolicyRuleSpec>(snapshot.OrderedRules.Count);
        foreach (var rule in snapshot.OrderedRules)
        {
            try
            {
                rules.Add(PolicyRuleMatcher.Parse(rule));
            }
            catch (AgentProtocolException error)
            {
                throw new AgentProtocolException("invalid_snapshot_rule", error.Message);
            }
        }

        return new BoundPolicySnapshot(
            snapshot.Version,
            snapshot.Preset,
            snapshot.Workspace.WorkspaceId,
            snapshot.Workspace.VolumeSerialNumber,
            snapshot.Workspace.RootFileId,
            snapshot.Workspace.DisplayIdentityHash,
            rules,
            DateTimeOffset.Parse(snapshot.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot));
    }
}

public sealed record PolicyRequestInvariants(
    bool SchemaValid = true,
    bool RequestBound = true,
    bool NotExpired = true,
    bool IntentHashValid = true)
{
    public static PolicyRequestInvariants Valid { get; } = new();
}

public sealed record PolicyEvaluation(
    string Decision,
    string ReasonCode,
    string? MatchedRuleId,
    bool AnalyzerRiskPending = false);

/// <summary>
/// Authoritative decision point: non-overridable protocol invariants first, then ordered
/// last-match-wins evaluation over the bound snapshot, then preset fallbacks.
/// </summary>
public sealed class PolicySnapshotEvaluator
{
    public PolicyEvaluation Evaluate(
        BoundPolicySnapshot snapshot,
        PolicyContext context,
        PolicyRequestInvariants invariants,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invariants);

        if (!invariants.SchemaValid)
        {
            return Deny("schema_invalid");
        }

        if (!invariants.RequestBound)
        {
            return Deny("request_unbound");
        }

        if (!invariants.NotExpired)
        {
            return Deny("request_expired");
        }

        if (!invariants.IntentHashValid)
        {
            return Deny("intent_hash_invalid");
        }

        if (snapshot.HasInvalidMatcher)
        {
            return Deny("matcher_invalid");
        }

        string? matchedDecision = null;
        string? matchedRuleId = null;
        foreach (var rule in snapshot.OrderedRules)
        {
            if (PolicyRuleMatcher.Matches(rule, context, now))
            {
                matchedDecision = rule.Decision;
                matchedRuleId = rule.RuleId;
            }
        }

        if (matchedDecision is not null)
        {
            return matchedDecision switch
            {
                "allow" => new PolicyEvaluation("allow", "rule_matched", matchedRuleId),
                "deny" => Deny("rule_denied", matchedRuleId),
                _ => Ask("rule_asks", matchedRuleId),
            };
        }

        return Fallback(snapshot.Preset, context);
    }

    private static PolicyEvaluation Fallback(string preset, PolicyContext context)
    {
        if (string.Equals(preset, "full_access", StringComparison.Ordinal))
        {
            return new PolicyEvaluation("allow", "full_access_fallback", MatchedRuleId: null);
        }

        return StandardFallback(context);
    }

    internal static PolicyEvaluation StandardFallback(PolicyContext context)
    {
        if (string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal))
        {
            return TerminalFallback(context);
        }

        if (!string.Equals(context.NetworkIntent, PolicyNetworkIntents.None, StringComparison.Ordinal))
        {
            return Ask("network_intent");
        }

        if (context.Target is { } target)
        {
            if (target.IsProtected)
            {
                return Ask("protected_path");
            }

            if (target.IsOutsideWorkspace)
            {
                return Ask("outside_workspace");
            }
        }

        switch (context.OperationClass)
        {
            case PolicyOperationClasses.Delete:
                return Ask("delete_requires_approval");
            case PolicyOperationClasses.Restore:
                return Ask("restore_once");
            case PolicyOperationClasses.Read:
            case PolicyOperationClasses.List:
            case PolicyOperationClasses.Glob:
            case PolicyOperationClasses.Search:
            case PolicyOperationClasses.Write:
            case PolicyOperationClasses.Edit:
            case PolicyOperationClasses.Patch:
            case PolicyOperationClasses.Discovery:
                if (context.Target is { } insideTarget && !insideTarget.IsInsideWorkspace)
                {
                    return Ask("outside_workspace");
                }

                return Allow("standard_workspace_allow");
            default:
                return Ask("unclassified_operation");
        }
    }

    private static PolicyEvaluation TerminalFallback(PolicyContext context)
    {
        if (string.Equals(context.ToolId, "terminal.shell", StringComparison.Ordinal))
        {
            return Ask("terminal_requires_rule");
        }

        var form = RecognizeTerminalForm(context.ResolvedExecutable, context.Argv);
        switch (form)
        {
            case TerminalForm.Unknown:
                return Ask("unknown_executable");
            case TerminalForm.GitMutation:
                return Ask("git_mutation");
            default:
                return new PolicyEvaluation(
                    "allow",
                    "recognized_safe_form",
                    MatchedRuleId: null,
                    AnalyzerRiskPending: true);
        }
    }

    internal enum TerminalForm
    {
        Unknown,
        RecognizedSafe,
        GitMutation,
    }

    internal static TerminalForm RecognizeTerminalForm(
        string? resolvedExecutable,
        IReadOnlyList<string> argv)
    {
        if (resolvedExecutable is null)
        {
            return TerminalForm.Unknown;
        }

        var baseName = Path.GetFileName(resolvedExecutable);
        baseName = baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^4]
            : baseName;

        // Phase-3 command analyzers own per-family parsing; these are conservative recognizers
        // for read/test/build/lint families only. No general shell understanding is claimed.
        switch (baseName.ToLowerInvariant())
        {
            case "rg":
                return TerminalForm.RecognizedSafe;
            case "git":
                var verb = FirstVerb(argv);
                if (verb is null)
                {
                    return TerminalForm.Unknown;
                }

                return GitReadOnlyVerbs.Contains(verb)
                    ? TerminalForm.RecognizedSafe
                    : GitMutationVerbs.Contains(verb)
                        ? TerminalForm.GitMutation
                        : TerminalForm.Unknown;
            case "dotnet":
                var dotnetVerb = FirstVerb(argv);
                return dotnetVerb is "build" or "test" or "format"
                    ? TerminalForm.RecognizedSafe
                    : TerminalForm.Unknown;
            case "pytest":
                return TerminalForm.RecognizedSafe;
            case "python" or "python3" or "py":
                for (var i = 0; i + 1 < argv.Count; i++)
                {
                    if (string.Equals(argv[i], "-m", StringComparison.Ordinal)
                        && argv[i + 1].StartsWith("pytest", StringComparison.Ordinal))
                    {
                        return TerminalForm.RecognizedSafe;
                    }
                }

                return TerminalForm.Unknown;
            default:
                return TerminalForm.Unknown;
        }
    }

    private static readonly HashSet<string> GitReadOnlyVerbs = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "branch", "rev-parse", "describe",
        "shortlog", "reflog", "ls-files", "grep", "cat-file",
    };

    private static readonly HashSet<string> GitMutationVerbs = new(StringComparer.Ordinal)
    {
        "commit", "push", "pull", "merge", "rebase", "reset", "checkout",
        "restore", "clean", "stash", "tag", "add", "rm", "mv", "cherry-pick", "revert",
    };

    private static string? FirstVerb(IReadOnlyList<string> argv)
    {
        foreach (var argument in argv)
        {
            if (argument.StartsWith("-"))
            {
                continue;
            }

            return argument;
        }

        return null;
    }

    private static PolicyEvaluation Allow(string reasonCode) =>
        new("allow", reasonCode, MatchedRuleId: null);

    private static PolicyEvaluation Ask(string reasonCode, string? matchedRuleId = null) =>
        new("ask", reasonCode, matchedRuleId);

    private static PolicyEvaluation Deny(string reasonCode, string? matchedRuleId = null) =>
        new("deny", reasonCode, matchedRuleId);
}
