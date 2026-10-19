using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentSnapshotReader(HarnessSqliteOptions options, HarnessPayloadStore payloads)
{
    private HarnessSqliteOptions Options => options;
    private HarnessPayloadStore Payloads => payloads;

    public async Task<SubagentSnapshot> ReadAsync(RunId root, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var parent = await RequireRootAsync(connection, transaction, root, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", root.Value);
        command.CommandText = "SELECT agent_id,internal_session_id,role,canceled,invocation_id FROM agent_subagents WHERE root_run_id=$root ORDER BY agent_id LIMIT 9";
        var identities = new List<(SubagentRecord Agent, string Invocation)>();
        using (var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                var role = (SubagentRole)rows.GetInt32(2);
                if (!Enum.IsDefined(role) || !SubagentContractValidator.IsValidId(rows.GetString(0))) Invalid();
                identities.Add((new(rows.GetString(0), root, new(rows.GetString(1)), role, rows.GetInt32(3) != 0), rows.GetString(4)));
            }
        }
        if (identities.Count > SubagentLimits.MaxIdentities) Invalid();
        var agents = new List<SubagentSnapshotEntry>();
        foreach (var (identity, invocation) in identities)
        {
            var attempts = await ReadAttemptsAsync(connection, transaction, identity, ct).ConfigureAwait(false);
            agents.Add(new(identity, await ReadTaskAsync(connection, transaction, identity, invocation, ct).ConfigureAwait(false),
                attempts, await ReadResultsAsync(connection, transaction, identity, ct).ConfigureAwait(false)));
        }
        command.CommandText = "SELECT agent_id,stage FROM agent_subagent_workspace_allocations WHERE root_run_id=$root ORDER BY agent_id LIMIT 9";
        var preparations = new List<SubagentPreparationSnapshot>();
        var allIds = identities.Select(i => i.Agent.AgentId).ToHashSet(StringComparer.Ordinal);
        using (var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var count = 0;
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = rows.GetString(0);
                var stage = (SubagentWorkspaceAllocationStage)rows.GetInt32(1);
                if (++count > SubagentLimits.MaxIdentities || !SubagentContractValidator.IsValidId(id) || !Enum.IsDefined(stage)) Invalid();
                if (allIds.Add(id)) preparations.Add(new(id, stage));
            }
        }
        if (allIds.Count > SubagentLimits.MaxIdentities) Invalid();
        command.CommandText = """
            SELECT count(*) FROM agent_internal_runs i LEFT JOIN agent_subagent_attempts a ON a.run_id=i.run_id
            LEFT JOIN agent_subagents s ON s.agent_id=a.agent_id
            WHERE i.root_run_id=$root AND (s.agent_id IS NULL OR s.root_run_id<>$root)
            """;
        var unavailable = checked((int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L));
        if (unavailable > SubagentLimits.MaxIdentities * SubagentLimits.MaxAttempts) Invalid();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1)";
        var canceled = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
        return new(parent, canceled, agents.AsReadOnly(), preparations.AsReadOnly(), unavailable);
    }

    private static async Task<AgentRun> RequireRootAsync(SqliteConnection connection, SqliteTransaction transaction, RunId root, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(root.Value)) throw new HarnessStoreException("subagent_arguments_invalid");
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, root, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$root)
                OR EXISTS(SELECT 1 FROM agent_subagent_attempts WHERE run_id=$root)
                OR EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=$session)
                OR EXISTS(SELECT 1 FROM agent_internal_runs i JOIN agent_runs r ON r.run_id=i.run_id WHERE r.session_id=$session)
                OR EXISTS(SELECT 1 FROM agent_session_tombstones WHERE session_id=$session)
            """;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$session", run.SessionId.Value);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
            throw new HarnessStoreException("subagent_root_not_found");
        return run;
    }

    private static void Invalid() => throw new HarnessStoreException("subagent_snapshot_invalid");
}
