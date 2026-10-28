using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Maintenance;

/// <summary>Explicit, offline maintenance for the durable Harness SQLite profile.</summary>
public sealed partial class DatabaseMaintenanceService
{
    private const string DatabaseFileName = "agent_harness.db";
    private const string KeyFileName = "harness_key.dat";
    private const string ManifestFileName = "manifest.json";
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HarnessSqliteOptions _options;
    private readonly string _keyPath;
    private readonly Action<string>? _restoreStepObserver;
    private readonly Func<string, long>? _availableSpaceProvider;

    public DatabaseMaintenanceService(HarnessSqliteOptions options, string keyPath)
        : this(options, keyPath, null, null)
    {
    }

    internal DatabaseMaintenanceService(
        HarnessSqliteOptions options,
        string keyPath,
        Action<string>? restoreStepObserver,
        Func<string, long>? availableSpaceProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _keyPath = Path.GetFullPath(keyPath);
        _restoreStepObserver = restoreStepObserver;
        _availableSpaceProvider = availableSpaceProvider;
    }

    public string DatabasePath => _options.DatabasePath;

    public async Task<DatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var dbPath = _options.DatabasePath;
        var dbInfo = new FileInfo(dbPath);
        var walInfo = new FileInfo(dbPath + "-wal");
        var directory = Path.GetDirectoryName(dbPath)!;
        var available = AvailableFreeSpace(directory);
        if (!dbInfo.Exists)
        {
            return new DatabaseMaintenanceStatus(
                false, 0, 0, walInfo.Exists ? walInfo.Length : 0, 0, 0, available,
                new Dictionary<string, long>(), "not_checked");
        }

        await using var connection = await OpenReadOnlyAsync(dbPath, cancellationToken).ConfigureAwait(false);
        var version = await ReadLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        var pages = await ReadLongAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        var freePages = await ReadLongAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        var rowCounts = await ReadTableCountsAsync(connection, cancellationToken).ConfigureAwait(false);
        return new DatabaseMaintenanceStatus(
            true,
            checked((int)version),
            dbInfo.Length,
            walInfo.Exists ? walInfo.Length : 0,
            pages,
            freePages,
            available,
            rowCounts,
            "not_checked");
    }

    public async Task<DatabaseMaintenanceResult> BackupAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new DatabaseMaintenanceException("database_backup_destination_exists");
        if (!File.Exists(_options.DatabasePath))
            throw new DatabaseMaintenanceException("database_missing");

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var sourceBytes = new FileInfo(_options.DatabasePath).Length
            + (File.Exists(_options.DatabasePath + "-wal") ? new FileInfo(_options.DatabasePath + "-wal").Length : 0);
        var keyBytes = File.Exists(_keyPath) ? new FileInfo(_keyPath).Length : 0;
        var requiredBytes = checked(Math.Max(16L * 1024 * 1024, sourceBytes + keyBytes));
        if (AvailableFreeSpace(parent) < requiredBytes)
            throw new DatabaseMaintenanceException("database_backup_insufficient_space");
        var staging = Path.Combine(parent, ".ru-database-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var keyBefore = File.Exists(_keyPath) ? await HashFileAsync(_keyPath, cancellationToken).ConfigureAwait(false) : null;
            await CreateSqliteSnapshotAsync(_options.DatabasePath, Path.Combine(staging, DatabaseFileName), cancellationToken)
                .ConfigureAwait(false);
            var keyAfter = File.Exists(_keyPath) ? await HashFileAsync(_keyPath, cancellationToken).ConfigureAwait(false) : null;
            if (!string.Equals(keyBefore, keyAfter, StringComparison.Ordinal))
                throw new DatabaseMaintenanceException("database_key_changed_during_backup");
            if (keyBefore is not null)
            {
                var stagedKeyPath = Path.Combine(staging, KeyFileName);
                File.Copy(_keyPath, stagedKeyPath, overwrite: false);
                var stagedKeyHash = await HashFileAsync(stagedKeyPath, cancellationToken).ConfigureAwait(false);
                if (!FixedTimeHexEquals(keyBefore, stagedKeyHash))
                    throw new DatabaseMaintenanceException("database_key_changed_during_backup");
            }

            var snapshotVersion = await ReadSchemaVersionAsync(Path.Combine(staging, DatabaseFileName), cancellationToken)
                .ConfigureAwait(false);
            await ValidateDatabaseAsync(Path.Combine(staging, DatabaseFileName), cancellationToken, requireCurrentVersion: true)
                .ConfigureAwait(false);
            var manifest = new BackupManifest(
                ManifestVersion,
                snapshotVersion,
                DateTimeOffset.UtcNow,
                await HashFileAsync(Path.Combine(staging, DatabaseFileName), cancellationToken).ConfigureAwait(false),
                keyBefore);
            await File.WriteAllTextAsync(
                Path.Combine(staging, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, destination);
            return new DatabaseMaintenanceResult("backup", "created", destination);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public async Task<DatabaseMaintenanceResult> VerifyAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var bundle = await ValidateBackupAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        return new DatabaseMaintenanceResult("verify", "valid", bundle.RootPath);
    }

    public async Task<DatabaseMaintenanceResult> CompactAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.DatabasePath))
            throw new DatabaseMaintenanceException("database_missing");
        var info = new FileInfo(_options.DatabasePath);
        var walBytes = File.Exists(_options.DatabasePath + "-wal")
            ? new FileInfo(_options.DatabasePath + "-wal").Length
            : 0;
        var requiredSpace = checked(Math.Max(64L * 1024 * 1024, (info.Length + walBytes) * 2));
        var available = AvailableFreeSpace(_options.DatabasePath);
        if (available < requiredSpace)
            throw new DatabaseMaintenanceException("database_compact_insufficient_space");

        await using (var connection = await OpenReadWriteAsync(_options.DatabasePath, cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 0)
                    throw new DatabaseMaintenanceException("database_busy");
            }
            // A separate statement observes VACUUM failures rather than suppressing
            // later batch errors while disposing the checkpoint result reader.
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ValidateDatabaseAsync(_options.DatabasePath, cancellationToken, requireCurrentVersion: true)
            .ConfigureAwait(false);
        return new DatabaseMaintenanceResult("compact", "complete", _options.DatabasePath);
    }

    private static async Task<long> ReadSchemaVersionAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadWriteAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeHexEquals(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        try
        {
            var a = Convert.FromHexString(left);
            var b = Convert.FromHexString(right);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException) { return false; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private long AvailableFreeSpace(string path) => _availableSpaceProvider?.Invoke(path)
        ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    internal sealed record BackupManifest(int FormatVersion, long SchemaVersion, DateTimeOffset CreatedAtUtc, string DatabaseSha256, string? KeySha256);
    internal sealed record VerifiedBackup(string RootPath, string DatabasePath, string? KeyPath, BackupManifest Manifest);
}
