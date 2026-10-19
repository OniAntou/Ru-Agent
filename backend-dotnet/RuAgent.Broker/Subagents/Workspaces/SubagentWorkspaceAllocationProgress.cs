using System.Text.Json;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

internal static class SubagentWorkspaceAllocationProgress
{
    public static void Validate(string rootId, string agentId, ISubagentWorkspaceAllocationObserver? observer)
    {
        if (observer is null) return;
        var reservation = observer.Reservation;
        if (reservation.RootRunId != rootId || reservation.AgentId != agentId
            || !SubagentContractValidator.IsValidId(reservation.SnapshotId)
            || !reservation.SnapshotId.StartsWith("snapshot_", StringComparison.Ordinal)
            || reservation.Stage != SubagentWorkspaceAllocationStage.Reserved
            || reservation.SnapshotJson is not null || reservation.WorktreeManifestJson is not null)
            throw new AgentProtocolException("subagent_allocation_invalid");
    }

    public static Task SnapshotAsync(ISubagentWorkspaceAllocationObserver? observer, SubagentSnapshot snapshot,
        SubagentWorkspaceAllocationStage stage, CancellationToken ct) => observer is null ? Task.CompletedTask
            : observer.RecordAsync(observer.Reservation with { Stage = stage,
                SnapshotJson = JsonSerializer.Serialize(snapshot with { Files = [] }) }, ct);

    public static Task WorktreeAsync(ISubagentWorkspaceAllocationObserver? observer, SubagentWorkspaceManifest manifest,
        SubagentWorkspaceAllocationStage stage, CancellationToken ct)
    {
        if (observer is null) return Task.CompletedTask;
        var minimal = manifest with { Snapshot = manifest.Snapshot with { Files = [] } };
        return observer.RecordAsync(observer.Reservation with { Stage = stage,
            SnapshotJson = JsonSerializer.Serialize(minimal.Snapshot), WorktreeManifestJson = JsonSerializer.Serialize(minimal) }, ct);
    }
}
