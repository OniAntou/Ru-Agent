using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    private async Task<SqliteConnection> OpenStoreAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isMigrated)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                if (!await BusinessSqliteDatabase.HasMigratedAsync(connection, transaction, "sessions", cancellationToken).ConfigureAwait(false))
                {
                    await ImportLegacySessionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await BusinessSqliteDatabase.MarkMigratedAsync(connection, transaction, "sessions", cancellationToken).ConfigureAwait(false);
                }
                transaction.Commit();
                _isMigrated = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ImportLegacySessionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_sessionDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidSessionId(id) || new FileInfo(path).Length > MaximumSessionFileBytes) continue;
            var bytes = await ReadLegacyBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var document = ParseDocument(bytes, id);
            if (document is null) continue;
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT 1 FROM business_sessions WHERE session_id = $id UNION ALL SELECT 1 FROM agent_session_tombstones WHERE session_id = $id LIMIT 1;";
            existing.Parameters.AddWithValue("$id", id);
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) continue;
            await WriteUnlockedAsync(document, connection, transaction, cancellationToken).ConfigureAwait(false);
            await InsertMessagesAsync(document.Messages, id, 0, connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (document.SourceMessageLedger is not null)
            {
                foreach (var (sourceId, fingerprint) in document.SourceMessageLedger)
                {
                    await using var receipt = connection.CreateCommand();
                    receipt.Transaction = transaction;
                    receipt.CommandText = "INSERT OR IGNORE INTO business_session_source_receipts(session_id, source_message_id, fingerprint) VALUES ($id, $source, $fingerprint);";
                    receipt.Parameters.AddWithValue("$id", id);
                    receipt.Parameters.AddWithValue("$source", sourceId);
                    receipt.Parameters.AddWithValue("$fingerprint", fingerprint);
                    await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<byte[]> ReadLegacyBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumSessionFileBytes) throw new InvalidDataException("session_file_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
