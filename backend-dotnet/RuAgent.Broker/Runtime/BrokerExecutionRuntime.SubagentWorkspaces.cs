using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Broker.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// Trusted Host seam; the state directory is configuration, never a model argument.
    /// Parent cancellation owns the entire capture/provision operation.
    /// </summary>
    public Task<SubagentWorkspaceManifest> ProvisionSubagentWorkspaceAsync(
        string parentRunId, string agentId, string stateDirectory, CancellationToken ct,
        BrokerPolicySnapshotWire? parentCeiling = null, ISubagentWorkspaceAllocationObserver? observer = null) => UnderSubagentArtifactsAsync(() =>
            ProvisionSubagentWorkspaceCoreAsync(parentRunId, agentId, stateDirectory, ct, parentCeiling, observer), ct);

    private async Task<SubagentWorkspaceManifest> ProvisionSubagentWorkspaceCoreAsync(
        string parentRunId, string agentId, string stateDirectory, CancellationToken ct,
        BrokerPolicySnapshotWire? parentCeiling, ISubagentWorkspaceAllocationObserver? observer)
    {
        SubagentWorkspaceAllocationProgress.Validate(parentRunId, agentId, observer);
        BrokerRunLease parent;
        BrokerRunLease captureParent;
        RunToolCancellationRegistry.Registration operation;
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!SubagentContractValidator.IsValidId(agentId) || _delegations.ContainsKey(parentRunId)
                || !_session.TryGetRunLease(parentRunId, out var lease) || lease is null
                || lease.Lifetime.IsRevoked || lease.IsExpired(DateTimeOffset.UtcNow) || lease.Preset == "plan")
                throw new AgentProtocolException("subagent_delegation_denied");
            parent = lease;
            var ceiling = parentCeiling is null ? null : DelegationParentPolicy.ValidateCeiling(parent, parentCeiling);
            captureParent = ceiling is null ? parent : parent with
            { Snapshot = ceiling, SnapshotVersion = ceiling.Version, SnapshotHash = ceiling.Sha256 };
            operation = _runCancellations.Register(parentRunId, ct);
        }
        finally { _lifecycleGate.Release(); }

        using (operation)
        {
            var snapshot = await new SubagentSnapshotCapture().CaptureAsync(captureParent, stateDirectory, operation.Token, observer)
                .ConfigureAwait(false);
            var workspace = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, agentId, operation.Token, observer)
                .ConfigureAwait(false);
            await _lifecycleGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                if (parent.Lifetime.IsRevoked || !_session.TryGetRunLease(parentRunId, out var current) || current is null
                    || current.Lifetime != parent.Lifetime
                    || current.IsExpired(DateTimeOffset.UtcNow) || current.SessionId != parent.SessionId
                    || current.CanonicalRoot != parent.CanonicalRoot || current.SnapshotVersion != parent.SnapshotVersion
                    || current.SnapshotHash != parent.SnapshotHash)
                    throw new AgentProtocolException("subagent_parent_revoked");
                return workspace;
            }
            finally { _lifecycleGate.Release(); }
        }
    }
}
