using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

public sealed partial class SubagentWorktreeProvisioner
{
    public async Task<SubagentWorkspaceManifest> ProvisionAsync(SubagentSnapshot snapshot, string agentId, CancellationToken ct,
        ISubagentWorkspaceAllocationObserver? observer = null)
    {
        SubagentWorkspaceAllocationProgress.Validate(snapshot.ParentRunId, agentId, observer);
        SubagentWorkspaceManifest? registered = null;
        try
        {
            return await ProvisionCoreAsync(snapshot, agentId, value => registered = value, ct, observer).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            // Only a successfully captured worktree/Git identity proves what
            // can be reclaimed. An interrupted Git registration stays unproven.
            if (registered is not null)
                SubagentWorkspaceFailureCleanup.Run(token => SubagentWorkspaceReclaimer.Reclaim(registered,
                    Path.GetDirectoryName(Path.GetDirectoryName(snapshot.BaseDirectory))!, token),
                    "subagent_provision_cleanup_incomplete", original);
            throw;
        }
    }
}
