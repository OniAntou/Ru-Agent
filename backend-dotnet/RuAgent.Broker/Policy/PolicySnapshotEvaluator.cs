using System.Globalization;
using RuAgent.Broker.Policy.Commands;
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
/// last-match-wins evaluation over the bound snapshot, then preset fallbacks. Terminal
/// fallbacks are classified by the versioned command analyzer registry.
/// </summary>
public sealed class PolicySnapshotEvaluator
{
    private readonly CommandAnalyzerRegistry _analyzers;
    private readonly ISessionDirectoryGrantRegistry _directoryGrants;

    public PolicySnapshotEvaluator(
        ISessionDirectoryGrantRegistry? directoryGrants = null,
        CommandAnalyzerRegistry? analyzers = null)
    {
        _directoryGrants = directoryGrants ?? new SessionDirectoryGrantRegistry();
        _analyzers = analyzers ?? new CommandAnalyzerRegistry();
    }

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

        var boundary = context.Target ?? context.WorkingDirectory;
        if (boundary is { IsProtected: true })
        {
            return Deny("protected_path");
        }

        if (boundary is { IsOutsideWorkspace: true }
            && !_directoryGrants.Contains(context.SessionId, boundary.CanonicalPath))
        {
            return Ask("external_directory_required");
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

        return EffectiveFallback(context);
    }

    private PolicyEvaluation EffectiveFallback(PolicyContext context)
    {
        if (string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal))
        {
            var timeoutMs = context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs;
            if (timeoutMs > TerminalLimits.HardMaxTimeoutMs)
            {
                return Deny("timeout_exceeds_hard_max");
            }

            return TerminalFallback(context);
        }

        return Allow("standard_workspace_allow");
    }

    private PolicyEvaluation TerminalFallback(PolicyContext context)
    {
        var analysis = context.TerminalAnalysis
            ?? _analyzers.Analyze(context.ResolvedExecutable, context.Argv);
        return analysis.OperationClass switch
        {
            TerminalOperationClass.ReadOnly
                or TerminalOperationClass.Test
                or TerminalOperationClass.Build
                or TerminalOperationClass.Lint
                or TerminalOperationClass.GitMutation
                or TerminalOperationClass.ExternalMutation => new PolicyEvaluation(
                    "allow",
                    "recognized_safe_form",
                    MatchedRuleId: null,
                    AnalyzerRiskPending: true),
            TerminalOperationClass.DirectDelete => Ask("terminal_delete_requires_approval"),
            _ => Ask("terminal_unknown_executable"),
        };
    }

    private static PolicyEvaluation Allow(string reasonCode) =>
        new("allow", reasonCode, MatchedRuleId: null);

    private static PolicyEvaluation Ask(string reasonCode, string? matchedRuleId = null) =>
        new("ask", reasonCode, matchedRuleId);

    private static PolicyEvaluation Deny(string reasonCode, string? matchedRuleId = null) =>
        new("deny", reasonCode, matchedRuleId);
}
