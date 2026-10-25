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
/// deny/ask/allow evaluation over the bound snapshot, then preset fallbacks. Terminal
/// fallbacks use the canonical launch directory independently of command syntax.
/// </summary>
public sealed class PolicySnapshotEvaluator
{
    private readonly ISessionDirectoryGrantRegistry _directoryGrants;
    private readonly TerminalIntentClassifier _terminalClassifier;

    public PolicySnapshotEvaluator(
        ISessionDirectoryGrantRegistry? directoryGrants = null)
    {
        _directoryGrants = directoryGrants ?? new SessionDirectoryGrantRegistry();
        _terminalClassifier = new TerminalIntentClassifier();
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
        if (context.PatchTargets.Count == 0) return EvaluateInner(snapshot, context, invariants, now);
        var parent = EvaluateInner(snapshot, context, invariants, now);
        if (parent.Decision == "deny") return parent;
        PolicyEvaluation? ask = parent.Decision == "ask" ? parent : null;
        foreach (var target in context.PatchTargets)
        {
            var decision = EvaluateInner(snapshot, target, invariants, now);
            if (decision.Decision == "deny") return decision;
            if (decision.Decision == "ask") ask ??= decision;
        }
        return ask ?? new PolicyEvaluation("allow", "patch_targets_allowed", null);
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

        // A background task does not block the turn, so it is bounded by its
        // own lifetime cap instead of the foreground hard maximum.
        if (IsTerminal(context)
            && (context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs)
                > (context.Background ? TerminalLimits.BackgroundMaxLifetimeMs : TerminalLimits.HardMaxTimeoutMs))
        {
            return Deny("timeout_exceeds_hard_max");
        }

        var structuredTarget = context.Target;
        // Catalog-protected targets (harness key/database files, runtime
        // descriptors, user-added RU_PROTECTED_PATHS entries) are a preset-level
        // guard: plan and standard deny them outright, while full_access is
        // literally full and evaluates them like any other target.
        if (structuredTarget is { IsProtected: true }
            && !AgentPolicyProfileCatalog.OpensProtectedPaths(AgentPolicyProfileCatalog.Get(snapshot.Preset)))
        {
            return Deny("protected_path");
        }

        // The ordered rules are resolved before the external-directory gate so a
        // user-authored deny wins on every target, including one outside the
        // workspace. Precedence is deny, then ask, then allow regardless of
        // creation order, matching Claude-style rule semantics: a remembered
        // allow can never override an explicit ask or deny. An allow still
        // cannot widen pre-rule invariants (plan gate, the preset's structured
        // protected-path guard, timeouts); outside the workspace it is honored
        // as a bounded folder grant whose matcher must carry the path pattern.
        string? matchedDecision = null;
        string? matchedRuleId = null;
        foreach (var rule in snapshot.OrderedRules)
        {
            if (!PolicyRuleMatcher.Matches(rule, context, now))
            {
                continue;
            }

            if (string.Equals(rule.Decision, "deny", StringComparison.Ordinal))
            {
                matchedDecision = "deny";
                matchedRuleId = rule.RuleId;
                break;
            }

            if (string.Equals(rule.Decision, "ask", StringComparison.Ordinal))
            {
                matchedDecision = "ask";
                matchedRuleId = rule.RuleId;
            }
            else if (matchedDecision is null)
            {
                matchedDecision = rule.Decision;
                matchedRuleId = rule.RuleId;
            }
        }

        if (string.Equals(matchedDecision, "deny", StringComparison.Ordinal))
        {
            return Deny("rule_denied", matchedRuleId);
        }

        // Plan is a read-only preset: every mutation is denied outright, even when
        // a remembered rule would allow it. Mainstream plan modes work the same
        // way -- the agent researches and proposes, and the user switches mode to
        // execute -- so no approval prompt appears mid-plan.
        if (IsPlanPreset(snapshot) && IsPlanGatedCall(context))
        {
            return Deny("plan_preset_read_only", matchedRuleId);
        }

        if (string.Equals(matchedDecision, "ask", StringComparison.Ordinal))
        {
            return Ask("rule_asks", matchedRuleId);
        }

        if (structuredTarget is { IsOutsideWorkspace: true })
        {
            // A matched allow rule grants external structured access: this is how
            // a durable project rule saved by allow_always (or a remembered
            // session pattern) turns an approved folder into standing access
            // bounded by the rule's own matcher. Deny rules and ask rules were
            // resolved above, the plan read-only gate still applied, and the
            // approval flow is still the only way such a rule can exist.
            if (string.Equals(matchedDecision, "allow", StringComparison.Ordinal))
            {
                return new PolicyEvaluation("allow", "external_directory_rule_allow", matchedRuleId);
            }

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
            return matchedDecision switch
            {
                "allow" => new PolicyEvaluation("allow", "rule_matched", matchedRuleId),
                _ => Ask("rule_asks", matchedRuleId),
            };
        }

        // Above the standard window the protocol requires an explicit approval before
        // execution, unless the profile already auto-allows arbitrary terminal
        // (full_access, the skip-all mode) or the call is a background task: a
        // background call returns immediately and is bounded by its lifetime cap,
        // so the blocking-window gate does not apply. It is a profile-level gate,
        // so it is applied after the ordered rules: an explicit allow or deny the
        // user wrote still decides, and the hard maximum and the preset's
        // structured protected-path guard have already been enforced above.
        if (IsTerminal(context)
            && !context.Background
            && (context.TimeoutMs ?? TerminalLimits.DefaultTimeoutMs) > TerminalLimits.StandardApprovalThresholdMs
            && AgentPolicyProfileCatalog.Get(snapshot.Preset).DecisionFor(AgentPolicySubject.ExternalTerminal)
                != AgentPolicyDecision.Allow)
        {
            return Ask("terminal_timeout_requires_approval");
        }

        return ProfileFallback(snapshot, context);
    }

    private static bool IsPlanPreset(BoundPolicySnapshot snapshot) =>
        string.Equals(snapshot.Preset, BrokerPolicyContracts.PlanPresetWireValue, StringComparison.Ordinal);

    private static bool IsStandardPreset(BoundPolicySnapshot snapshot) =>
        string.Equals(snapshot.Preset, BrokerPolicyContracts.StandardPresetWireValue, StringComparison.Ordinal);

    private static bool IsPlanGatedCall(PolicyContext context) =>
        string.Equals(context.OperationClass, PolicyOperationClasses.Write, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Edit, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Delete, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Patch, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Mutation, StringComparison.Ordinal)
        || string.Equals(context.OperationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal);

    private PolicyEvaluation ProfileFallback(BoundPolicySnapshot snapshot, PolicyContext context)
    {
        // ADR 0025: starting an MCP server or calling its tool runs a user-chosen program
        // with the user's rights, the same authority as a terminal launch outside the
        // workspace, so it takes that subject's decision (plan deny, standard ask,
        // full_access allow). Listing the declared servers only reads the user's file.
        if (string.Equals(context.ToolId, "mcp", StringComparison.Ordinal))
        {
            return string.Equals(context.ToolOp, "list_servers", StringComparison.Ordinal)
                ? Allow("mcp_list_servers_allow")
                : ProfileDecision(snapshot, AgentPolicySubject.ExternalTerminal, "mcp_call_requires_approval");
        }

        if (IsTerminal(context))
        {
            var classification = _terminalClassifier.Classify(context);
            var subject = classification.IsWorkspaceLaunch
                ? AgentPolicySubject.WorkspaceTerminal
                : AgentPolicySubject.ExternalTerminal;
            if (classification.ReasonCode is "terminal_cwd_untrusted" or "terminal_tool_invalid" or "terminal_mode_invalid")
                return Deny(classification.ReasonCode);
            return ProfileDecision(snapshot, subject, classification.ReasonCode);
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
