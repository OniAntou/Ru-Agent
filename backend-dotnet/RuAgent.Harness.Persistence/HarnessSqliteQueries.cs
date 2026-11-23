using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

internal sealed record ExistingRun(AgentRun Run, string NormalizedRequestSha256);

internal static class HarnessSqliteQueries
{
    internal static async Task<AgentRun?> GetRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {HarnessSqliteMapper.RunColumns}
            FROM agent_runs
            WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var run = HarnessSqliteMapper.ReadRun(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        return await WithRunFailureAsync(connection, transaction, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the bounded failure fingerprint to a failed run. It lives in its own table so
    /// the migration that introduced it stays replay-safe; a run failed by name has none.
    /// </summary>
    internal static async Task<AgentRun> WithRunFailureAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (!run.Status.State.IsTerminal())
        {
            return run;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, site FROM agent_run_failures WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", run.RunId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return run;
        }

        var failure = new RunFailureFingerprint(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
        return run with { TerminalFailure = failure };
    }

    internal static async Task<ExistingRun?> FindIdempotentRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        string idempotencyKeySha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {HarnessSqliteMapper.RunColumns}, normalized_request_sha256
            FROM agent_runs
            WHERE session_id = $session_id
              AND idempotency_key_sha256 = $idempotency_key
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKeySha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Named, not positional: the run projection gained columns and this one
        // used to be ordinal 18.
        return new ExistingRun(
            HarnessSqliteMapper.ReadRun(reader),
            reader.GetString(reader.GetOrdinal("normalized_request_sha256")));
    }

    internal static Task<AgentRun?> FindActiveSessionRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        FindActiveAsync(
            connection,
            transaction,
            "session_id",
            sessionId.Value,
            cancellationToken);

    internal static async Task<bool> WorkspaceExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agent_workspaces WHERE workspace_id = $workspace_id";
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    internal static async Task<bool> SessionDeletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agent_session_tombstones WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<AgentRun?> FindActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {HarnessSqliteMapper.RunColumns}
            FROM agent_runs
            WHERE {column} = $value
              AND terminal_at_utc IS NULL
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var run = HarnessSqliteMapper.ReadRun(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        return await WithRunFailureAsync(connection, transaction, run, cancellationToken).ConfigureAwait(false);
    }
}
