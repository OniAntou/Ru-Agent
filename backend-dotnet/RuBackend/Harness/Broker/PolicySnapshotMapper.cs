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
    public static BrokerPolicySnapshotWire ToWire(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new BrokerPolicySnapshotWire(
            Version: snapshot.Version,
            Preset: PresetName(snapshot.Preset),
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

    private static string PresetName(AgentPermissionPreset preset) => preset switch
    {
        AgentPermissionPreset.Standard => BrokerPolicyContracts.StandardPresetWireValue,
        AgentPermissionPreset.FullAccess => BrokerPolicyContracts.FullAccessPresetWireValue,
        AgentPermissionPreset.Trusted => BrokerPolicyContracts.TrustedPresetWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), $"Unknown preset '{preset}'."),
    };

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
