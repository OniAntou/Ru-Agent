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
    private readonly ISessionDirectoryGrantRegistry _directoryGrants;
    private readonly TerminalIntentClassifier _terminalClassifier;

    public PolicySnapshotEvaluator(
        ISessionDirectoryGrantRegistry? directoryGrants = null,
        CommandAnalyzerRegistry? analyzers = null)
    {
        _directoryGrants = directoryGrants ?? new SessionDirectoryGrantRegistry();
        _terminalClassifier = new TerminalIntentClassifier(analyzers);
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
        return EvaluateInner(snapshot, context, invariants, now);
    }

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

        if (IsTerminal(context)
            && (context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs) > TerminalLimits.HardMaxTimeoutMs)
        {
            return Deny("timeout_exceeds_hard_max");
        }

        var structuredTarget = context.Target;
        if (structuredTarget is { IsProtected: true })
        {
            return Deny("protected_path");
        }

        // The ordered rules are resolved before the external-directory gate so a
        // user-authored deny wins on every target, including one outside the
        // workspace. Only the deny verdict is taken from them here: an allow or
        // ask rule still cannot grant the external access the profile asks for.
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

        if (string.Equals(matchedDecision, "deny", StringComparison.Ordinal))
        {
            return Deny("rule_denied", matchedRuleId);
        }

        if (structuredTarget is { IsOutsideWorkspace: true })
        {
            var external = ProfileDecision(
                snapshot,
                AgentPolicySubject.ExternalStructuredFile,
                "external_directory_required");
            if (string.Equals(external.Decision, "ask", StringComparison.Ordinal)
                && _directoryGrants.Contains(context.SessionId, structuredTarget.CanonicalPath))
            {
                return Allow("external_directory_grant_allow");
            }

            return external;
        }

        if (matchedDecision is not null)
        {
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

        // Above the standard window the protocol requires an explicit approval before
        // execution. It is a profile-level gate, so it is applied after the ordered
        // rules: an explicit allow or deny the user wrote still decides, and the hard
        // maximum and the protected-path deny have already been enforced above.
        if (IsTerminal(context)
            && (context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs) > TerminalLimits.StandardApprovalThresholdMs)
        {
            return Ask("terminal_timeout_requires_approval");
        }

        return ProfileFallback(snapshot, context);
    }

    private static bool IsPlanPreset(BoundPolicySnapshot snapshot) =>
        string.Equals(snapshot.Preset, BrokerPolicyContracts.PlanPresetWireValue, StringComparison.Ordinal);

    private static bool IsPlanGatedCall(PolicyContext context) =>
        string.Equals(context.OperationClass, PolicyOperationClasses.Write, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Edit, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Delete, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Patch, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Mutation, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal);

    private PolicyEvaluation ProfileFallback(BoundPolicySnapshot snapshot, PolicyContext context)
    {
        if (IsTerminal(context))
        {
            var classification = _terminalClassifier.Classify(context);
            var subject = classification.IsBoundedLocal
                ? AgentPolicySubject.BoundedLocalTerminal
                : AgentPolicySubject.RiskyTerminal;
            var reasonCode = classification.IsBoundedLocal
                ? "recognized_safe_form"
                : classification.ReasonCode;
            return ProfileDecision(snapshot, subject, reasonCode, AnalyzerRiskPending: true);
        }

        if (IsWebTool(context))
        {
            var reasonCode = IsPlanPreset(snapshot) ? "plan_web_allow" : "standard_web_allow";
            return ProfileDecision(snapshot, AgentPolicySubject.Web, reasonCode);
        }

        var workspaceSubject = context.OperationClass is PolicyOperationClasses.Read
            or PolicyOperationClasses.List
            or PolicyOperationClasses.Glob
            or PolicyOperationClasses.Search
            or PolicyOperationClasses.Discovery
            ? AgentPolicySubject.WorkspaceRead
            : AgentPolicySubject.WorkspaceMutation;
        return ProfileDecision(snapshot, workspaceSubject, "standard_workspace_allow");
    }

    private static PolicyEvaluation ProfileDecision(
        BoundPolicySnapshot snapshot,
        AgentPolicySubject subject,
        string reasonCode,
        bool AnalyzerRiskPending = false)
    {
        var decision = AgentPolicyProfileCatalog.Get(snapshot.Preset).DecisionFor(subject);
        var wireDecision = decision switch
        {
            AgentPolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
            AgentPolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
            AgentPolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
            _ => throw new AgentProtocolException("invalid_decision"),
        };
        var effectiveReason = wireDecision == BrokerPolicyContracts.AllowWireValue
            && string.Equals(snapshot.Preset, BrokerPolicyContracts.FullAccessPresetWireValue, StringComparison.Ordinal)
                ? "full_access_allow"
                : wireDecision == BrokerPolicyContracts.DenyWireValue
                    && string.Equals(snapshot.Preset, BrokerPolicyContracts.PlanPresetWireValue, StringComparison.Ordinal)
                    ? "plan_preset_requires_approval"
                    : reasonCode;
        return new PolicyEvaluation(
            wireDecision,
            effectiveReason,
            MatchedRuleId: null,
            AnalyzerRiskPending: AnalyzerRiskPending && wireDecision == BrokerPolicyContracts.AllowWireValue);
    }

    private static PolicyEvaluation Allow(string reasonCode) =>
        new("allow", reasonCode, MatchedRuleId: null);

    private static bool IsWebTool(PolicyContext context) =>
        string.Equals(context.ToolId, "web_search", StringComparison.Ordinal)
        || string.Equals(context.ToolId, "web_fetch", StringComparison.Ordinal);

    private static bool IsTerminal(PolicyContext context) =>
        string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal);

    private static PolicyEvaluation Ask(string reasonCode, string? matchedRuleId = null) =>
        new("ask", reasonCode, matchedRuleId);

    private static PolicyEvaluation Deny(string reasonCode, string? matchedRuleId = null) =>
        new("deny", reasonCode, matchedRuleId);
}
