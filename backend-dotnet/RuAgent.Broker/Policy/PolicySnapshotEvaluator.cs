using System.Globalization;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Tools.Terminal;
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
    private readonly IWorkspaceCleanlinessProvider? _cleanliness;

    public PolicySnapshotEvaluator(
        ISessionDirectoryGrantRegistry? directoryGrants = null,
        CommandAnalyzerRegistry? analyzers = null,
        IWorkspaceCleanlinessProvider? cleanliness = null)
    {
        _directoryGrants = directoryGrants ?? new SessionDirectoryGrantRegistry();
        _analyzers = analyzers ?? new CommandAnalyzerRegistry();
        _cleanliness = cleanliness;
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

        var verdict = EvaluateInner(snapshot, context, invariants, now);
        if (!string.Equals(verdict.Decision, "ask", StringComparison.Ordinal)
            || !IsStandardAllowableAsk(verdict.ReasonCode, context)
            || !string.Equals(snapshot.Preset, BrokerPolicyContracts.StandardPresetWireValue, StringComparison.Ordinal)
            || _cleanliness?.IsWorkspaceClean(context.CanonicalWorkspaceRoot) != true)
        {
            return verdict;
        }

        return new PolicyEvaluation("allow", "standard_clean_workspace_allow", verdict.MatchedRuleId);
    }

    /// <summary>
    /// The Ask codes a clean standard workspace may auto-allow. Timeouts,
    /// explicit user rules and protocol invariants always keep asking: only
    /// in-workspace terminal/delete prompts, outside-workspace read-only
    /// probes, and clean-workspace unknown executables are lifted. Anything
    /// that mutates outside the workspace still asks no matter how clean the
    /// repo is, and networked terminal calls stay gated.
    /// </summary>
    private static bool IsStandardAllowableAsk(string reasonCode, PolicyContext context) =>
        reasonCode switch
        {
            "terminal_external_mutation_requires_approval" or "terminal_unknown_executable"
                => string.Equals(context.NetworkIntent, PolicyNetworkIntents.None, StringComparison.Ordinal)
                    && !context.NetworkDeclared,
            "terminal_delete_requires_approval"
                => context.Target is { IsInsideWorkspace: true },
            "external_directory_required" => IsReadOnlyProbe(context),
            _ => false,
        };

    /// <summary>
    /// Read-only probes over an outside-workspace directory: seeing bytes is
    /// not consent to change them, so the clean-workspace lift stops here.
    /// </summary>
    private static bool IsReadOnlyProbe(PolicyContext context) =>
        context.OperationClass is PolicyOperationClasses.Read
            or PolicyOperationClasses.List
            or PolicyOperationClasses.Glob
            or PolicyOperationClasses.Search
            or PolicyOperationClasses.Discovery;

    private PolicyEvaluation EvaluateInner(
        BoundPolicySnapshot snapshot,
        PolicyContext context,
        PolicyRequestInvariants invariants,
        DateTimeOffset now)
    {

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

        var boundary = context.Target ?? context.WorkingDirectory;
        if (boundary is { IsProtected: true })
        {
            return Deny("protected_path");
        }

        if (boundary is { IsOutsideWorkspace: true }
            && !_directoryGrants.Contains(context.SessionId, boundary.CanonicalPath))
        {
            if (string.Equals(snapshot.Preset, BrokerPolicyContracts.FullAccessPresetWireValue, StringComparison.Ordinal))
            {
                return Allow("full_access_allow");
            }

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
            // Explicit deny wins on every preset, including plan: the plan
            // gate below only downgrades allows to asks, never denies.
            if (string.Equals(matchedDecision, "deny", StringComparison.Ordinal))
            {
                return Deny("rule_denied", matchedRuleId);
            }

            // plan is a read-only gate: every mutation asks, nothing auto-allows.
            if (IsPlanPreset(snapshot) && IsPlanGatedCall(context))
            {
                return Ask("plan_preset_requires_approval", matchedRuleId);
            }

            return matchedDecision switch
            {
                "allow" => new PolicyEvaluation("allow", "rule_matched", matchedRuleId),
                _ => Ask("rule_asks", matchedRuleId),
            };
        }

        // full_access allows everything except hard denies (handled above).
        // Web approvals are handled below. No approval surfaces.
        if (string.Equals(snapshot.Preset, BrokerPolicyContracts.FullAccessPresetWireValue, StringComparison.Ordinal))
        {
            if (IsWebTool(context))
            {
                return Allow("full_access_allow");
            }

            if (string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal))
            {
                var timeoutMs = context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs;
                if (timeoutMs > TerminalLimits.HardMaxTimeoutMs)
                {
                    return Deny("timeout_exceeds_hard_max");
                }

                return new PolicyEvaluation(
                    "allow",
                    "full_access_allow",
                    MatchedRuleId: null,
                    AnalyzerRiskPending: true);
            }

            return Allow("full_access_allow");
        }

        // Web tools ask on plan only: standard browses without approval
        // (full_access already allowed above).
        if (IsWebTool(context))
        {
            if (IsPlanPreset(snapshot))
            {
                return Ask("plan_preset_requires_approval");
            }

            if (string.Equals(snapshot.Preset, BrokerPolicyContracts.StandardPresetWireValue, StringComparison.Ordinal))
            {
                return Allow("standard_web_allow");
            }

            return Ask("web_approval_required");
        }

        return EffectiveFallback(snapshot, context);
    }

    private static bool IsPlanPreset(BoundPolicySnapshot snapshot) =>
        string.Equals(snapshot.Preset, BrokerPolicyContracts.PlanPresetWireValue, StringComparison.Ordinal);

    /// <summary>
    /// The plan-gated surface: every mutation-class tool call (write/edit/bash/web)
    /// needs an explicit decision. Reads never gate here.
    /// </summary>
    private static bool IsPlanGatedCall(PolicyContext context) =>
        string.Equals(context.OperationClass, PolicyOperationClasses.Write, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Edit, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Delete, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Patch, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Mutation, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal)
        || IsWebTool(context);

    private PolicyEvaluation EffectiveFallback(BoundPolicySnapshot snapshot, PolicyContext context)
    {
        // plan is a read-only gate: gated calls ask instead of taking the
        // standard allow paths below; reads still flow.
        if (IsPlanPreset(snapshot) && IsPlanGatedCall(context))
        {
            return Ask("plan_preset_requires_approval");
        }

        if (string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal))
        {
            var timeoutMs = context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs;
            if (timeoutMs > TerminalLimits.HardMaxTimeoutMs)
            {
                return Deny("timeout_exceeds_hard_max");
            }

            if (timeoutMs > TerminalLimits.StandardApprovalThresholdMs)
            {
                return Ask("terminal_timeout_requires_approval");
            }

            return TerminalFallback(context);
        }

        return Allow("standard_workspace_allow");
    }

    private PolicyEvaluation TerminalFallback(PolicyContext context)
    {
        // Single terminal verdict. bash mode=shell always asks: arbitrary script
        // text cannot be auto-classified safe, so the AST verdict only feeds the
        // approval preview, never an allow. Keep in sync with TerminalAutoPolicy
        // (run payloads) and TerminalAdapter (execution leg).
        if (IsShellMode(context))
        {
            var shellAnalysis = context.TerminalAnalysis
                ?? _analyzers.Analyze(context.ResolvedExecutable, context.Argv);
            return shellAnalysis.OperationClass switch
            {
                TerminalOperationClass.ExternalMutation => Ask("terminal_external_mutation_requires_approval"),
                TerminalOperationClass.DirectDelete => Ask("terminal_delete_requires_approval"),
                _ => Ask("terminal_unknown_executable"),
            };
        }

        var analysis = context.TerminalAnalysis
            ?? _analyzers.Analyze(context.ResolvedExecutable, context.Argv);
        // External mutations (network egress, package installs, push/fetch/pull)
        // always require explicit approval. The analyzer verdict only feeds the
        // approval preview; it never auto-allows side effects outside the workspace.
        // Keep in sync with TerminalAutoPolicy: auto = read/test/build/lint/git-mutation.
        if (TerminalAutoPolicy.IsAutoExecutable(analysis.OperationClass))
        {
            return new PolicyEvaluation(
                "allow",
                "recognized_safe_form",
                MatchedRuleId: null,
                AnalyzerRiskPending: true);
        }
        return analysis.OperationClass switch
        {
            TerminalOperationClass.ExternalMutation => Ask("terminal_external_mutation_requires_approval"),
            TerminalOperationClass.DirectDelete => Ask("terminal_delete_requires_approval"),
            _ => Ask("terminal_unknown_executable"),
        };
    }

    private static PolicyEvaluation Allow(string reasonCode) =>
        new("allow", reasonCode, MatchedRuleId: null);

    private static bool IsWebTool(PolicyContext context) =>
        string.Equals(context.ToolId, "web_search", StringComparison.Ordinal)
        || string.Equals(context.ToolId, "web_fetch", StringComparison.Ordinal);

    private static bool IsShellMode(PolicyContext context) =>
        string.Equals(context.ToolId, "bash", StringComparison.Ordinal)
        && string.Equals(context.BashMode, "shell", StringComparison.Ordinal);

    private static PolicyEvaluation Ask(string reasonCode, string? matchedRuleId = null) =>
        new("ask", reasonCode, matchedRuleId);

    private static PolicyEvaluation Deny(string reasonCode, string? matchedRuleId = null) =>
        new("deny", reasonCode, matchedRuleId);
}
