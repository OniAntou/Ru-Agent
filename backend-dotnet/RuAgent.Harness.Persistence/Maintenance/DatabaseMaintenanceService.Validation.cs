using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Maintenance;

public sealed partial class DatabaseMaintenanceService
{
    internal async Task<VerifiedBackup> ValidateBackupAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        var root = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(root))
            throw new DatabaseMaintenanceException("database_backup_not_found");
        var manifestPath = Path.Combine(root, ManifestFileName);
        var dbPath = Path.Combine(root, DatabaseFileName);
        var keyPath = Path.Combine(root, KeyFileName);
        BackupManifest? manifest;
        try
        {
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > 64 * 1024)
                throw new DatabaseMaintenanceException("database_backup_manifest_invalid");
            manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (DatabaseMaintenanceException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new DatabaseMaintenanceException("database_backup_manifest_invalid", exception);
        }

        if (manifest is null || manifest.FormatVersion != ManifestVersion
            || manifest.SchemaVersion != HarnessSqliteSchema.CurrentVersion
            || !File.Exists(dbPath))
            throw new DatabaseMaintenanceException("database_backup_version_unsupported");
        var dbHash = await HashFileAsync(dbPath, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeHexEquals(dbHash, manifest.DatabaseSha256))
            throw new DatabaseMaintenanceException("database_backup_hash_mismatch");
        var actualKeyHash = File.Exists(keyPath)
            ? await HashFileAsync(keyPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!FixedTimeHexEquals(actualKeyHash, manifest.KeySha256)
            || ((actualKeyHash is null) != (manifest.KeySha256 is null)))
            throw new DatabaseMaintenanceException("database_backup_key_mismatch");

        await ValidateDatabaseAsync(dbPath, cancellationToken, requireCurrentVersion: true).ConfigureAwait(false);
        if (await HasProtectedRowsAsync(dbPath, cancellationToken).ConfigureAwait(false))
        {
            if (!File.Exists(keyPath))
                throw new DatabaseMaintenanceException("harness_key_missing");
            var key = ReadOpaqueDpapiKey(keyPath);
            try { await ValidateProtectedDataKeyAsync(dbPath, key, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else if (File.Exists(keyPath))
        {
            var key = ReadOpaqueDpapiKey(keyPath);
            CryptographicOperations.ZeroMemory(key);
        }
        return new VerifiedBackup(root, dbPath, File.Exists(keyPath) ? keyPath : null, manifest);
    }

    private static async Task CreateSqliteSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = await OpenReadOnlyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await using var destination = await OpenReadWriteAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        await destination.CloseAsync().ConfigureAwait(false);
    }

    internal static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken, bool requireCurrentVersion)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        var version = await ReadLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (requireCurrentVersion && version != HarnessSqliteSchema.CurrentVersion)
            throw new DatabaseMaintenanceException("database_schema_version_unsupported");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (requireCurrentVersion)
        {
            if (!await HarnessSqliteSchemaValidator.ValidateAsync(connection, cancellationToken).ConfigureAwait(false))
                throw new DatabaseMaintenanceException("database_schema_invalid");
        }
        else
        {
            await using var quickCheck = connection.CreateCommand();
            quickCheck.CommandText = "PRAGMA quick_check;";
            var quickResult = await quickCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(quickResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new DatabaseMaintenanceException("database_integrity_failed");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new DatabaseMaintenanceException("database_foreign_key_integrity_failed");
        }

        if (requireCurrentVersion)
            await ValidateSessionSearchIndexAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasProtectedRowsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var tables = await ReadTableColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var (table, columns) in tables)
        {
            var encryptedColumns = columns.Where(column =>
                column.Contains("ciphertext", StringComparison.OrdinalIgnoreCase)
                || column.Contains("protected", StringComparison.OrdinalIgnoreCase)
                || column.Contains("authentication_tag", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (encryptedColumns.Length == 0) continue;
            var identifier = QuoteIdentifier(table);
            await using var command = connection.CreateCommand();
            var encryptedValuePredicate = string.Join(" OR ", encryptedColumns.Select(column =>
                $"{QuoteIdentifier(column)} IS NOT NULL"));
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {identifier} WHERE {encryptedValuePredicate} LIMIT 1);";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0)
                return true;
        }
        return false;
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadTableCountsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var list = connection.CreateCommand();
        list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (var name in names)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(name)};";
            counts.Add(name, (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L));
        }
        return counts;
    }

    private static async Task<Dictionary<string, string[]>> ReadTableColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var list = connection.CreateCommand();
        list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var names = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        foreach (var name in names)
        {
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = $"PRAGMA table_info({QuoteIdentifier(name)});";
            var columns = new List<string>();
            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) columns.Add(reader.GetString(1));
            tables.Add(name, columns.ToArray());
        }
        return tables;
    }

    private static byte[] ReadOpaqueDpapiKey(string keyPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(keyPath);
            const int headerLength = 5;
            if (bytes.Length <= headerLength
                || bytes.Length > 64 * 1024
                || !bytes.AsSpan(0, headerLength).SequenceEqual("RUHK1"u8))
                throw new DatabaseMaintenanceException("harness_key_invalid");
            if (!OperatingSystem.IsWindows())
                throw new DatabaseMaintenanceException("database_key_verification_requires_windows");
            var protectedBytes = bytes.AsSpan(headerLength).ToArray();
            try
            {
                byte[] key;
                try { key = ProtectedData.Unprotect(protectedBytes, WindowsDpapiKeyStore.DpapiEntropy, DataProtectionScope.CurrentUser); }
                catch (CryptographicException)
                {
                    key = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                }
                if (key.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new DatabaseMaintenanceException("harness_key_invalid");
                }
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (DatabaseMaintenanceException) { throw; }
        catch (CryptographicException exception)
        {
            throw new DatabaseMaintenanceException("harness_key_unprotect_failed", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DatabaseMaintenanceException("harness_key_read_failed", exception);
        }
    }

}
