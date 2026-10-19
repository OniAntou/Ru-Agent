using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness.Subagents;

public sealed record PreparedSubagentWorkspace(string Root, PolicySnapshot Policy, string? ManifestJson);

public interface ISubagentWorkspaceFactory
{
    Task<PreparedSubagentWorkspace> CreateAsync(AgentRun parent, string parentRoot, PolicySnapshot parentPolicy,
        string agentId, SubagentRole role, CancellationToken ct);
}

public sealed class SubagentWorkspaceFactory(IToolGatewayFactory gateways, WorkspaceTrustService workspaces,
    string stateDirectory, SqliteSubagentWorkspaceAllocationStore allocations) : ISubagentWorkspaceFactory
{
    public async Task<PreparedSubagentWorkspace> CreateAsync(AgentRun parent, string parentRoot, PolicySnapshot parentPolicy,
        string agentId, SubagentRole role, CancellationToken ct)
    {
        if (role == SubagentRole.Review) return new(parentRoot, parentPolicy, null);
        if (role != SubagentRole.Coding || parent.Preset == AgentPermissionPreset.Plan)
            throw new SubagentConfigurationException("subagent_role_denied");
        var reservation = await allocations.ReserveAsync(parent.RunId, agentId, ct).ConfigureAwait(false);
        var manifest = await gateways.ProvisionSubagentWorkspaceAsync(parent.RunId, agentId, stateDirectory, ct,
            PolicySnapshotMapper.ToWire(parentPolicy), new AllocationObserver(reservation, allocations)).ConfigureAwait(false);
        if (!string.Equals(parentRoot, manifest.Snapshot.ParentRoot, StringComparison.OrdinalIgnoreCase)
            || manifest.Snapshot.ParentRunId != parent.RunId.Value || manifest.AgentId != agentId)
            throw new SubagentConfigurationException("subagent_workspace_invalid");
        // Registration stores encrypted identity only. It does not grant global trust;
        // the child derives authority from the parent through the Broker binding.
        var registration = await workspaces.EnsureTrustRequestAsync(manifest.WorkspaceRoot, ct).ConfigureAwait(false);
        var policy = PolicySnapshotCanonicalizer.Create(parentPolicy.Version, parentPolicy.Preset,
            registration.Workspace, parentPolicy.OrderedRules, parentPolicy.CreatedAt);
        return new(manifest.WorkspaceRoot, policy, JsonSerializer.Serialize(manifest));
    }

    private sealed class AllocationObserver(SubagentWorkspaceAllocation reservation, SqliteSubagentWorkspaceAllocationStore store)
        : ISubagentWorkspaceAllocationObserver
    {
        public SubagentWorkspaceAllocation Reservation => reservation;
        public Task RecordAsync(SubagentWorkspaceAllocation evidence, CancellationToken cancellationToken)
        {
            if (evidence.RootRunId != reservation.RootRunId || evidence.AgentId != reservation.AgentId || evidence.SnapshotId != reservation.SnapshotId)
                throw new SubagentConfigurationException("subagent_allocation_invalid");
            return store.RecordAsync(evidence, cancellationToken);
        }
    }
}
