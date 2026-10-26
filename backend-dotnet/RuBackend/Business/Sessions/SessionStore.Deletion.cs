using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken) =>
        await DeleteWithOutcomeAsync(sessionId, cancellationToken).ConfigureAwait(false) == SessionDeleteOutcome.Deleted;

    public async Task<SessionDeleteOutcome> DeleteWithOutcomeAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId)) return SessionDeleteOutcome.Failed;
        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM business_sessions WHERE session_id = $id;";
            command.Parameters.AddWithValue("$id", sessionId);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            transaction.Commit();
            return deleted ? SessionDeleteOutcome.Deleted : SessionDeleteOutcome.NotFound;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            return SessionDeleteOutcome.Failed;
        }
    }
}
