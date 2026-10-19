using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentWorkspaceAllocationStore
{
    /// <summary>Caller must have live Broker proof that filesystem allocation never started.
    /// Reserved stage alone is insufficient proof after an interruption.</summary>
    public async Task ReleaseUnallocatedAsync(SubagentWorkspaceAllocation reservation, CancellationToken ct)
    {
        if (reservation.Stage != SubagentWorkspaceAllocationStage.Reserved)
            throw new HarnessStoreException("subagent_allocation_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM agent_subagent_workspace_allocations
            WHERE root_run_id=$root AND agent_id=$agent AND snapshot_id=$snapshot AND stage=0
                AND NOT EXISTS(SELECT 1 FROM agent_subagents WHERE agent_id=$agent)
            """;
        command.Parameters.AddWithValue("$root", reservation.RootRunId);
        command.Parameters.AddWithValue("$agent", reservation.AgentId);
        command.Parameters.AddWithValue("$snapshot", reservation.SnapshotId);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new HarnessStoreException("subagent_preparation_incomplete");
    }
}
