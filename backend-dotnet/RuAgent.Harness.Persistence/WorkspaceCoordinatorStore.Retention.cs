using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

/// <summary>What one retention pass removed from the workspace change journal.</summary>
public sealed record WorkspaceJournalPruneResult(int RemovedByAge, int RemovedByBudget)
{
    public int Total => RemovedByAge + RemovedByBudget;
}

public sealed partial class WorkspaceCoordinatorStore
{
    /// <summary>
    /// Bounds the change journal. It drops settled entries (committed, restored or rejected) older
    /// than <paramref name="olderThanUtc"/>, then, per workspace, drops the oldest settled entries
    /// until the encrypted payloads fit in <paramref name="maxPayloadBytesPerWorkspace"/>. A
    /// prepared entry is an in-flight change and is never pruned. The journal exists so a recent
    /// change can be restored; without a bound it grew with every change to a watched file.
    /// </summary>
    public async Task<WorkspaceJournalPruneResult> PruneChangeJournalAsync(
        DateTimeOffset olderThanUtc,
        long maxPayloadBytesPerWorkspace,
        CancellationToken cancellationToken)
    {
        if (maxPayloadBytesPerWorkspace < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytesPerWorkspace));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            int byAge;
            await using (var age = connection.CreateCommand())
            {
                age.Transaction = transaction;
                age.CommandText = """
                    DELETE FROM workspace_change_journal
                    WHERE status IN ('committed', 'restored', 'rejected')
                      AND created_at_utc < $cutoff
                    """;
                age.Parameters.AddWithValue("$cutoff", olderThanUtc.ToUniversalTime().ToString("O"));
                byAge = await age.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int byBudget;
            await using (var budget = connection.CreateCommand())
            {
                budget.Transaction = transaction;
                budget.CommandText = """
                    DELETE FROM workspace_change_journal
                    WHERE change_id IN (
                        SELECT change_id FROM (
                            SELECT change_id,
                                   SUM(length(protected_payload)) OVER (
                                       PARTITION BY workspace_id
                                       ORDER BY commit_sequence DESC, created_at_utc DESC, change_id DESC) AS running
                            FROM workspace_change_journal
                            WHERE status IN ('committed', 'restored', 'rejected'))
                        WHERE running > $budget)
                    """;
                budget.Parameters.AddWithValue("$budget", maxPayloadBytesPerWorkspace);
                byBudget = await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceJournalPruneResult(byAge, byBudget);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException error)
        {
            throw new HarnessStoreException("workspace_journal_prune_failed", error);
        }
    }
}
