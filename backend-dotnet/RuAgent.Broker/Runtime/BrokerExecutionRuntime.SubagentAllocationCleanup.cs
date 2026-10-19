using System.Text.Json;
using RuAgent.Broker.Subagents;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>Reclaims retained preparation evidence without opening or reconstructing a child run.</summary>
    public Task ReclaimSubagentAllocationAsync(SubagentWorkspaceAllocation evidence, string stateDirectory, CancellationToken ct) =>
        UnderSubagentArtifactsAsync(async () =>
        {
            var snapshot = ValidateAllocation(evidence, stateDirectory);
            var owned = Path.Combine(stateDirectory, evidence.SnapshotId);
            await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session.HasRunWithinDirectory(owned)) throw new AgentProtocolException("subagent_cleanup_busy");
                if (evidence.Stage == SubagentWorkspaceAllocationStage.Reserved)
                {
                    if (OwnedDirectoryDeletion.Exists(owned)) throw new AgentProtocolException("subagent_cleanup_allocation_unproven");
                }
                else if (evidence.Stage == SubagentWorkspaceAllocationStage.RegisteringWorktree)
                    throw new AgentProtocolException("subagent_cleanup_registration_unknown");
                else if (evidence.Stage == SubagentWorkspaceAllocationStage.SnapshotAllocated)
                    SubagentWorkspaceReclaimer.ReclaimSnapshot(snapshot!, stateDirectory, ct);
                else
                {
                    var manifest = JsonSerializer.Deserialize<SubagentWorkspaceManifest>(evidence.WorktreeManifestJson!)!;
                    if (_session.HasRunWithinDirectory(manifest.GitDirectory)) throw new AgentProtocolException("subagent_cleanup_busy");
                    SubagentWorkspaceReclaimer.Reclaim(manifest, stateDirectory, ct);
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && error is not SubagentWorkspaceException)
            { throw new SubagentWorkspaceException("subagent_cleanup_incomplete", error); }
            finally { _lifecycleGate.Release(); }
        }, ct);

    private static SubagentSnapshot? ValidateAllocation(SubagentWorkspaceAllocation evidence, string stateDirectory)
    {
        try
        {
            if (!SubagentContractValidator.IsValidId(evidence.RootRunId) || !SubagentContractValidator.IsValidId(evidence.AgentId)
                || !SubagentContractValidator.IsValidId(evidence.SnapshotId) || !evidence.SnapshotId.StartsWith("snapshot_", StringComparison.Ordinal)
                || !Enum.IsDefined(evidence.Stage) || !Path.IsPathFullyQualified(stateDirectory)
                || (evidence.Stage == SubagentWorkspaceAllocationStage.Reserved) != (evidence.SnapshotJson is null)
                || (evidence.Stage >= SubagentWorkspaceAllocationStage.WorktreeRegistered) != (evidence.WorktreeManifestJson is not null)
                || evidence.SnapshotJson?.Length > SubagentWorkspaceAllocation.MaximumEvidenceBytes
                || evidence.WorktreeManifestJson?.Length > SubagentWorkspaceAllocation.MaximumEvidenceBytes)
                throw new AgentProtocolException("subagent_allocation_invalid");
            _ = Path.GetFullPath(stateDirectory);
            if (evidence.Stage == SubagentWorkspaceAllocationStage.Reserved) return null;
            var snapshot = JsonSerializer.Deserialize<SubagentSnapshot>(evidence.SnapshotJson!)
                ?? throw new AgentProtocolException("subagent_allocation_invalid");
            if (snapshot.SnapshotId != evidence.SnapshotId || snapshot.ParentRunId != evidence.RootRunId
                || snapshot.Files is null || snapshot.Files.Count != 0 || !Path.IsPathFullyQualified(snapshot.ParentRoot)
                || !Path.IsPathFullyQualified(snapshot.GitDirectory) || !Path.IsPathFullyQualified(snapshot.CommonDirectory)
                || DelegationPolicyEvaluator.Inside(snapshot.ParentRoot, stateDirectory)
                || !SamePath(snapshot.BaseDirectory, Path.Combine(stateDirectory, evidence.SnapshotId, "base")))
                throw new AgentProtocolException("subagent_allocation_invalid");
            _ = Path.GetFullPath(snapshot.ParentRoot);
            _ = Path.GetFullPath(snapshot.GitDirectory);
            _ = Path.GetFullPath(snapshot.CommonDirectory);
            if (evidence.Stage >= SubagentWorkspaceAllocationStage.WorktreeRegistered)
            {
                var manifest = JsonSerializer.Deserialize<SubagentWorkspaceManifest>(evidence.WorktreeManifestJson!)
                    ?? throw new AgentProtocolException("subagent_allocation_invalid");
                if (manifest.AgentId != evidence.AgentId || !SamePath(manifest.WorkspaceRoot,
                        Path.Combine(stateDirectory, evidence.SnapshotId, evidence.AgentId))
                    || !Path.IsPathFullyQualified(manifest.GitDirectory)
                    || JsonSerializer.Serialize(manifest.Snapshot) != evidence.SnapshotJson)
                    throw new AgentProtocolException("subagent_allocation_invalid");
                _ = Path.GetFullPath(manifest.GitDirectory);
            }
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new AgentProtocolException("subagent_allocation_invalid"); }
    }
}
