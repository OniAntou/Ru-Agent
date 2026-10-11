using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed record HarnessSessionArtifactDeleteResult(
    int ClosedActiveRuns,
    int DeletedRuns);

public interface IHarnessSessionArtifactStore
{
    Task MarkSessionDeletedAsync(
        string sessionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken);

    Task<HarnessSessionArtifactDeleteResult> CloseAndDeleteAsync(
        string sessionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListNonTerminalRunIdsAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public sealed class HarnessSessionArtifactStore : IHarnessSessionArtifactStore
{
    /// <summary>
    /// Non-terminal runs one delete pass cancels and unregisters. Past this the
    /// listing stops; the run rows are still deleted, and the runtime drive
    /// loop drops any leftover registration once its row is gone.
    /// </summary>
    public const int MaxNonTerminalRunsPerSession = 64;

    private readonly HarnessSqliteOptions _options;

    public HarnessSessionArtifactStore(HarnessSqliteOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Writes the session's tombstone before any artifact row is removed. Run
    /// creation checks it inside the insert transaction, so a create that
    /// passed the session-file read before the delete cannot insert afterwards.
    /// </summary>
    public async Task MarkSessionDeletedAsync(
        string sessionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        var health = await HarnessSqliteSchema.EnsureInitializedAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        if (!health.IsAvailable)
        {
            throw new HarnessStoreException(
                health.ErrorCode ?? "harness_store_unavailable");
        }

        try
        {
            await using var connection = await _options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_session_tombstones(session_id, deleted_at_utc)
                VALUES ($session_id, $deleted_at)
                ON CONFLICT(session_id) DO UPDATE SET deleted_at_utc = excluded.deleted_at_utc
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$deleted_at", HarnessSqliteMapper.Utc(deletedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_session_delete_failed", exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListNonTerminalRunIdsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        var health = await HarnessSqliteSchema.EnsureInitializedAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        if (!health.IsAvailable)
        {
            throw new HarnessStoreException(
                health.ErrorCode ?? "harness_store_unavailable");
        }

        try
        {
            await using var connection = await _options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT run_id
                FROM agent_runs
                WHERE session_id = $session_id
                  AND terminal_at_utc IS NULL
                ORDER BY created_at_utc DESC, run_id DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$limit", MaxNonTerminalRunsPerSession);
            var runIds = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runIds.Add(reader.GetString(0));
            }

            return runIds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_session_delete_failed", exception);
        }
    }

    public async Task<HarnessSessionArtifactDeleteResult> CloseAndDeleteAsync(
        string sessionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        var health = await HarnessSqliteSchema.EnsureInitializedAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        if (!health.IsAvailable)
        {
            throw new HarnessStoreException(
                health.ErrorCode ?? "harness_store_unavailable");
        }

        try
        {
            await using var connection = await _options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var deletedRuns = await CountRunsAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var close = connection.CreateCommand();
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE agent_runs
                SET state = $cancelled,
                    history_wait_kind = NULL,
                    version = version + 1,
                    updated_at_utc = $deleted_at,
                    terminal_at_utc = $deleted_at,
                    terminal_error_code = $reason
                WHERE session_id = $session_id
                  AND terminal_at_utc IS NULL
                """;
            close.Parameters.AddWithValue("$cancelled", (int)HarnessRunState.Cancelled);
            close.Parameters.AddWithValue("$deleted_at", HarnessSqliteMapper.Utc(deletedAt));
            close.Parameters.AddWithValue("$reason", "session_deleted");
            close.Parameters.AddWithValue("$session_id", sessionId);
            var closedRuns = await close.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var deleteRuns = connection.CreateCommand();
            deleteRuns.Transaction = transaction;
            deleteRuns.CommandText = "DELETE FROM agent_runs WHERE session_id = $session_id";
            deleteRuns.Parameters.AddWithValue("$session_id", sessionId);
            if (await deleteRuns.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)
                != deletedRuns)
            {
                throw new HarnessStoreException("harness_session_delete_conflict");
            }

            await using var deleteSetting = connection.CreateCommand();
            deleteSetting.Transaction = transaction;
            deleteSetting.CommandText =
                "DELETE FROM agent_session_settings WHERE session_id = $session_id";
            deleteSetting.Parameters.AddWithValue("$session_id", sessionId);
            await deleteSetting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteWorkState = connection.CreateCommand();
            deleteWorkState.Transaction = transaction;
            deleteWorkState.CommandText =
                "DELETE FROM agent_session_work_states WHERE session_id = $session_id";
            deleteWorkState.Parameters.AddWithValue("$session_id", sessionId);
            await deleteWorkState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new HarnessSessionArtifactDeleteResult(closedRuns, deletedRuns);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_session_delete_failed", exception);
        }
    }

    private static async Task<int> CountRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM agent_runs WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId);
        var value = (long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L);
        return checked((int)value);
    }

    private static void ValidateSessionId(string? sessionId)
    {
        if (sessionId is not { Length: >= 1 and <= 64 }
            || sessionId.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }
    }
}
