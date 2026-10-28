using Microsoft.Data.Sqlite;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Maintenance;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;

namespace RuBackend.DatabaseMaintenance;

internal static class DatabasePruneCommand
{
    private const int MaximumCandidates = 10_000;

    public static async Task<DatabaseMaintenanceResult> RunAsync(
        string databasePath,
        DataPaths dataPaths,
        int olderThanDays,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (olderThanDays is < 1 or > 36500)
            throw new DatabaseMaintenanceException("database_prune_age_invalid");
        if (!File.Exists(databasePath))
            return new DatabaseMaintenanceResult("prune", apply ? "complete" : "dry_run", SessionIds: []);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToString("O");
        var candidates = await FindEligibleSessionsAsync(databasePath, cutoff, cancellationToken).ConfigureAwait(false);
        if (candidates.Count >= MaximumCandidates)
            throw new DatabaseMaintenanceException("database_prune_candidate_limit_exceeded");
        if (!apply)
            return new DatabaseMaintenanceResult("prune", "dry_run", SessionIds: candidates);

        var artifacts = new HarnessSessionArtifactStore(new HarnessSqliteOptions(databasePath));
        var sessions = new SessionStore(dataPaths);
        var coordinator = new HarnessSessionDeletionCoordinator(artifacts, sessions, TimeProvider.System);
        var deleted = new List<string>(candidates.Count);
        foreach (var sessionId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck before applying so an external writer that ignores the
            // profile lease cannot make an active or pending session eligible.
            var stillEligible = await FindEligibleSessionsAsync(
                databasePath,
                cutoff,
                cancellationToken,
                onlySessionId: sessionId).ConfigureAwait(false);
            if (stillEligible.Count == 0) continue;
            HarnessSessionDeletionOutcome outcome;
            try
            {
                outcome = await coordinator.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or SessionHistoryException or InvalidDataException)
            {
                throw new DatabaseMaintenanceException("database_prune_delete_failed", exception);
            }
            if (outcome is HarnessSessionDeletionOutcome.ArtifactsPending or HarnessSessionDeletionOutcome.SessionDeleteFailed)
                throw new DatabaseMaintenanceException("database_prune_delete_failed");
            if (outcome == HarnessSessionDeletionOutcome.Deleted) deleted.Add(sessionId);
        }
        return new DatabaseMaintenanceResult("prune", "complete", SessionIds: deleted);
    }

    private static async Task<IReadOnlyList<string>> FindEligibleSessionsAsync(
        string databasePath,
        string cutoff,
        CancellationToken cancellationToken,
        string? onlySessionId = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.session_id
                FROM business_sessions AS s
                WHERE s.updated_at_utc < $cutoff
                  AND ($session_id IS NULL OR s.session_id = $session_id)
                  AND NOT EXISTS (
                      SELECT 1 FROM agent_runs AS r
                      WHERE r.session_id = s.session_id AND r.terminal_at_utc IS NULL
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM agent_conversation_outbox AS o
                      WHERE o.session_id = s.session_id AND o.delivery_state IN (0, 1, 3)
                  )
                ORDER BY s.updated_at_utc, s.session_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$session_id", (object?)onlySessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", MaximumCandidates);
            var eligible = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                eligible.Add(reader.GetString(0));
            return eligible;
        }
    }
}
