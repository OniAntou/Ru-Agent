using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Settings;

public sealed partial class SettingsStore
{
    private async Task<SqliteConnection> OpenStoreAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isMigrated)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                if (!await BusinessSqliteDatabase.HasMigratedAsync(connection, transaction, "settings", cancellationToken).ConfigureAwait(false))
                {
                    var raw = await ReadLegacySettingsAsync(cancellationToken).ConfigureAwait(false);
                    if (raw is null) return connection;
                    foreach (var field in CredentialFields.Concat(RetiredSettingsFields)) raw.Remove(field);
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO business_settings(id, document) VALUES (1, $document) ON CONFLICT(id) DO NOTHING;";
                    command.Parameters.AddWithValue("$document", raw.ToJsonString());
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await BusinessSqliteDatabase.MarkMigratedAsync(connection, transaction, "settings", cancellationToken).ConfigureAwait(false);
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

    private async Task<JsonObject?> ReadLegacySettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsFile)) return [];
        if (new FileInfo(_settingsFile).Length > MaximumSettingsFileBytes) return null;
        try
        {
            var bytes = await ReadBoundedBytesAsync(_settingsFile, MaximumSettingsFileBytes, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }
}
