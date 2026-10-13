using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    public async Task<bool> CleanupEmptyAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId)) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var current = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction)
                .ConfigureAwait(false);
            if (current is not null && current.Messages.Count == 0)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM business_sessions WHERE session_id = $id;";
                command.Parameters.AddWithValue("$id", sessionId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            return true;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
