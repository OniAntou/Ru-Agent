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
        // Single source of truth: PolicySnapshotMapper owns the Host -> Broker
        // wire projection so hash-compatible mapping cannot drift.
        // Kept here only for existing callers/tests.
        return Harness.Broker.PolicySnapshotMapper.ToWire(snapshot);
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
