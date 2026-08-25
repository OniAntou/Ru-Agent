using System.Globalization;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Policy;

public sealed class HarnessPolicySnapshotService
{
    private readonly HarnessSqliteOptions _options;
    private readonly IHarnessKeyStore _keyStore;
    private readonly ITimeProvider _time;

    public HarnessPolicySnapshotService(HarnessSqliteOptions options, IHarnessKeyStore keyStore, ITimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public Task<PolicySnapshot> LoadAsync(
        WorkspaceIdentity workspace,
        AgentPermissionPreset preset,
        RunId? runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var loader = new PolicySnapshotLoader(_options, _keyStore);
        return loader.LoadAsync(workspace, preset, runId, _time.UtcNow, cancellationToken);
    }

    public static BrokerPolicySnapshotWire ToWire(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var wirePreset = snapshot.Preset == AgentPermissionPreset.FullAccess
            ? BrokerPolicyContracts.FullAccessPresetWireValue
            : BrokerPolicyContracts.StandardPresetWireValue;

        var wireWorkspace = new BrokerPolicyWorkspaceIdentityWire(
            snapshot.Workspace.WorkspaceId.Value,
            snapshot.Workspace.VolumeSerialNumber,
            snapshot.Workspace.RootFileId,
            snapshot.Workspace.DisplayIdentityHash);

        var wireRules = snapshot.OrderedRules.Select(rule => new BrokerPolicyRuleWire(
            RuleId: rule.RuleId.Value,
            Scope: rule.Scope switch
            {
                PolicyRuleScope.Global => BrokerPolicyContracts.GlobalScopeWireValue,
                PolicyRuleScope.Project => BrokerPolicyContracts.ProjectScopeWireValue,
                PolicyRuleScope.Run => BrokerPolicyContracts.RunScopeWireValue,
                PolicyRuleScope.Once => BrokerPolicyContracts.OnceScopeWireValue,
                _ => throw new ArgumentOutOfRangeException(nameof(rule.Scope)),
            },
            Decision: rule.Decision switch
            {
                PolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
                PolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
                PolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
                _ => throw new ArgumentOutOfRangeException(nameof(rule.Decision)),
            },
            Matcher: new BrokerPolicyMatcherWire(
                ToolId: rule.Matcher.ToolId,
                ToolVersion: rule.Matcher.ToolVersion,
                CanonicalPathPattern: rule.Matcher.CanonicalPathPattern,
                ResolvedExecutable: rule.Matcher.ResolvedExecutable,
                ArgvPattern: rule.Matcher.ArgvPattern,
                CwdPattern: rule.Matcher.CwdPattern,
                OperationClass: rule.Matcher.OperationClass,
                NetworkIntent: rule.Matcher.NetworkIntent),
            CreationOrder: rule.CreationOrder,
            ExpiresAt: rule.ExpiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
            .ToList();

        return new BrokerPolicySnapshotWire(
            Version: snapshot.Version,
            Preset: wirePreset,
            Workspace: wireWorkspace,
            OrderedRules: wireRules,
            CreatedAt: snapshot.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    public static string ComputeProtocolHash(PolicySnapshot snapshot)
    {
        var wire = ToWire(snapshot);
        return BrokerPolicyContracts.ComputeCanonicalSnapshotHash(wire);
    }

    public static bool ParityCheck(PolicySnapshot snapshot)
    {
        var coreHash = snapshot.Sha256;
        var protocolHash = ComputeProtocolHash(snapshot);
        return string.Equals(coreHash, protocolHash, StringComparison.Ordinal);
    }

    public static string ComputeCoreHash(PolicySnapshot snapshot) => snapshot.Sha256;
}
