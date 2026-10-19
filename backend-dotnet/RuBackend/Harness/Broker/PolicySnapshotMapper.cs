using System.Globalization;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Broker;

/// <summary>
/// Projects the Host-side domain policy snapshot onto the Broker wire contract.
/// The projection is hash-compatible with the Core canonicalizer:
/// <see cref="BrokerPolicyContracts.ComputeCanonicalSnapshotHash"/> over the
/// mapped wire equals <see cref="PolicySnapshot.Sha256"/> for snapshots
/// produced by <see cref="PolicySnapshotCanonicalizer"/>.
/// </summary>
public static class PolicySnapshotMapper
{
    public static PolicySnapshot FromWire(BrokerPolicySnapshotWire snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BrokerPolicyContracts.ValidateSnapshot(snapshot);
        var workspace = new global::RuAgent.Harness.Core.Workspace.WorkspaceIdentity(new(snapshot.Workspace.WorkspaceId),
            snapshot.Workspace.VolumeSerialNumber, snapshot.Workspace.RootFileId, snapshot.Workspace.DisplayIdentityHash);
        var rules = snapshot.OrderedRules.Select(rule => new PolicyRule(new(rule.RuleId), rule.Scope switch
        {
            "global" => PolicyRuleScope.Global,
            "project" => PolicyRuleScope.Project,
            "run" => PolicyRuleScope.Run,
            "once" => PolicyRuleScope.Once,
            _ => throw new ArgumentException("Invalid policy scope."),
        }, rule.Decision switch
        {
            "allow" => PolicyDecision.Allow,
            "ask" => PolicyDecision.Ask,
            "deny" => PolicyDecision.Deny,
            _ => throw new ArgumentException("Invalid policy decision."),
        }, new PolicyMatcher(rule.Matcher.ToolId, rule.Matcher.ToolVersion, rule.Matcher.CanonicalPathPattern,
            rule.Matcher.ResolvedExecutable, rule.Matcher.ArgvPattern, rule.Matcher.CwdPattern,
            rule.Matcher.OperationClass, rule.Matcher.NetworkIntent), rule.CreationOrder,
            rule.ExpiresAt is null ? null : DateTimeOffset.Parse(rule.ExpiresAt, CultureInfo.InvariantCulture))).ToArray();
        var preset = snapshot.Preset switch
        {
            "plan" => AgentPermissionPreset.Plan,
            "standard" => AgentPermissionPreset.Standard,
            "full_access" => AgentPermissionPreset.FullAccess,
            _ => throw new ArgumentException("Invalid permission preset."),
        };
        var recovered = PolicySnapshotCanonicalizer.Create(snapshot.Version, preset, workspace, rules,
            DateTimeOffset.Parse(snapshot.CreatedAt, CultureInfo.InvariantCulture));
        if (recovered.Sha256 != BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot))
            throw new ArgumentException("Policy recovery hash mismatch.");
        return recovered;
    }

    public static BrokerPolicySnapshotWire ToWire(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new BrokerPolicySnapshotWire(
            Version: snapshot.Version,
            Preset: AgentPermissionPresetMapper.ToWireValue(snapshot.Preset),
            Workspace: WorkspaceToWire(snapshot.Workspace),
            OrderedRules: RulesToWire(snapshot.OrderedRules),
            CreatedAt: FormatTimestamp(snapshot.CreatedAt));
    }

    private static BrokerPolicyWorkspaceIdentityWire WorkspaceToWire(
        global::RuAgent.Harness.Core.Workspace.WorkspaceIdentity workspace) => new(
            workspace.WorkspaceId.Value,
            workspace.VolumeSerialNumber,
            workspace.RootFileId,
            workspace.DisplayIdentityHash);

    private static IReadOnlyList<BrokerPolicyRuleWire> RulesToWire(
        IReadOnlyList<PolicyRule> rules)
    {
        var wireRules = new List<BrokerPolicyRuleWire>(rules.Count);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            wireRules.Add(new BrokerPolicyRuleWire(
                RuleId: rule.RuleId.Value,
                Scope: ScopeName(rule.Scope),
                Decision: DecisionName(rule.Decision),
                Matcher: MatcherToWire(rule.Matcher),
                CreationOrder: rule.CreationOrder,
                ExpiresAt: rule.ExpiresAt is { } expiresAt
                    ? FormatTimestamp(expiresAt)
                    : null));
        }

        return wireRules;
    }

    private static BrokerPolicyMatcherWire MatcherToWire(PolicyMatcher matcher) => new(
        ToolId: matcher.ToolId,
        ToolVersion: matcher.ToolVersion,
        CanonicalPathPattern: matcher.CanonicalPathPattern,
        ResolvedExecutable: matcher.ResolvedExecutable,
        ArgvPattern: matcher.ArgvPattern is null ? null : matcher.ArgvPattern.ToArray(),
        CwdPattern: matcher.CwdPattern,
        OperationClass: matcher.OperationClass,
        NetworkIntent: matcher.NetworkIntent);

    private static string ScopeName(PolicyRuleScope scope) => scope switch
    {
        PolicyRuleScope.Global => BrokerPolicyContracts.GlobalScopeWireValue,
        PolicyRuleScope.Project => BrokerPolicyContracts.ProjectScopeWireValue,
        PolicyRuleScope.Run => BrokerPolicyContracts.RunScopeWireValue,
        PolicyRuleScope.Once => BrokerPolicyContracts.OnceScopeWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), $"Unknown rule scope '{scope}'."),
    };

    private static string DecisionName(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
        PolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
        PolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), $"Unknown decision '{decision}'."),
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
