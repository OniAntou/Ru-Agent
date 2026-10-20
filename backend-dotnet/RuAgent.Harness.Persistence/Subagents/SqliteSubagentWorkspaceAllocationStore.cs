using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentWorkspaceAllocationStore(HarnessSqliteOptions options, WorkspaceJournalProtector protector)
{
    private WorkspaceJournalProtector Protector => protector;


    public async Task<SubagentWorkspaceAllocation> ReserveAsync(RunId rootId, string agentId, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(rootId.Value) || !SubagentContractValidator.IsValidId(agentId))
            throw new HarnessStoreException("subagent_arguments_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await RequireActiveRootAsync(connection, transaction, rootId, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_subagent_workspace_allocations WHERE agent_id=$agent)
                OR EXISTS(SELECT 1 FROM agent_subagents WHERE agent_id=$agent)
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
            throw new HarnessStoreException("subagent_preparation_incomplete");
        command.CommandText = """
            SELECT count(*) FROM (
                SELECT agent_id FROM agent_subagents WHERE root_run_id=$root
                UNION SELECT agent_id FROM agent_subagent_workspace_allocations WHERE root_run_id=$root)
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) >= SubagentLimits.MaxIdentities)
            throw new HarnessStoreException("subagent_identity_limit");
        var reservation = new SubagentWorkspaceAllocation(rootId.Value, agentId, "snapshot_" + Guid.NewGuid().ToString("N"),
            SubagentWorkspaceAllocationStage.Reserved);
        command.CommandText = """
            INSERT INTO agent_subagent_workspace_allocations(root_run_id,agent_id,snapshot_id,stage)
            VALUES($root,$agent,$snapshot,0)
            """;
        command.Parameters.AddWithValue("$snapshot", reservation.SnapshotId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return reservation;
    }

    private static async Task<AgentRun> RequireActiveRootAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId rootId, CancellationToken ct)
    {
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        if (root.State.IsTerminal() || root.Preset == AgentPermissionPreset.Plan)
            throw new HarnessStoreException("subagent_root_inactive");
        if (root.ModelStepsInWindow > SubagentLimits.FamilyModelSteps || root.ToolCallsInWindow > SubagentLimits.FamilyToolCalls)
            throw new HarnessStoreException("subagent_family_budget_exhausted");
        if (await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, root.SessionId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("session_deleted");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1)
                OR EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$root)
                OR EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=$session)
            """;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$session", root.SessionId.Value);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
            throw new HarnessStoreException("subagent_root_inactive");
        return root;
    }
}
