using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    public async Task<bool> IsInternalRunAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$run)
                OR EXISTS(SELECT 1 FROM agent_subagent_attempts WHERE run_id=$run)
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    public async Task<IReadOnlyList<RunId>> ListOwnedRunIdsAsync(RunId rootRunId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM agent_internal_runs WHERE root_run_id=$root ORDER BY run_id";
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var runs = new List<RunId>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) runs.Add(new(reader.GetString(0)));
        return runs.AsReadOnly();
    }

    private static async Task MarkInternalRunAsync(SqliteConnection connection, SqliteTransaction transaction, RunId runId, RunId rootRunId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.CommandText = "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($run,$root)";
        command.Parameters.AddWithValue("$run", runId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
