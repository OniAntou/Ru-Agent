using RuAgent.Broker.Runtime;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

public sealed partial class SubagentSnapshotCapture
{
    public async Task<SubagentSnapshot> CaptureAsync(BrokerRunLease parent, string stateDirectory, CancellationToken ct,
        ISubagentWorkspaceAllocationObserver? observer = null)
    {
        SubagentWorkspaceAllocationProgress.Validate(parent.RunId, observer?.Reservation.AgentId ?? "", observer);
        SnapshotAllocation? allocation = null;
        try
        {
            return await CaptureCoreAsync(parent, stateDirectory, value => allocation = value, ct, observer).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            // Capture has unwound its directory leases. Reclaim only the exact
            // allocation it created, including when the caller cancelled copying.
            // A different identity is retained; never guess its ownership.
            if (allocation is not null)
                SubagentWorkspaceFailureCleanup.Run(token => new OwnedDirectoryDeletion().Delete(
                    allocation.Directory, allocation.Identity, token), "subagent_snapshot_cleanup_incomplete", original);
            throw;
        }
    }

    private sealed record SnapshotAllocation(string Directory, string Identity);
}
