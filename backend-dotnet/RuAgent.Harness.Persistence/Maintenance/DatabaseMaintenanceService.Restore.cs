using System.Text.Json;

namespace RuAgent.Harness.Persistence.Maintenance;

public sealed partial class DatabaseMaintenanceService
{
    private const string RestoreJournalFileName = "database-restore-journal.json";
    private const string RestoreStagePrefix = ".ru-database-restore-";
    private const string PreRestorePrefix = "database-maintenance-pre-restore-";

    public static bool IsRestorePending(string databasePath) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, RestoreJournalFileName));

    public async Task<DatabaseMaintenanceResult> RestoreAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var backup = await ValidateBackupAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        var profileRoot = Path.GetDirectoryName(_options.DatabasePath)!;
        Directory.CreateDirectory(profileRoot);
        if (File.Exists(Path.Combine(profileRoot, RestoreJournalFileName)))
            throw new DatabaseMaintenanceException("database_restore_recovery_pending");
        var currentBytes = File.Exists(_options.DatabasePath) ? new FileInfo(_options.DatabasePath).Length : 0;
        var incomingBytes = new FileInfo(backup.DatabasePath).Length;
        var keyBytes = backup.KeyPath is null ? 0 : new FileInfo(backup.KeyPath).Length;
        var requiredBytes = checked(Math.Max(32L * 1024 * 1024, (currentBytes + incomingBytes) * 2 + keyBytes * 2));
        if (AvailableFreeSpace(profileRoot) < requiredBytes)
            throw new DatabaseMaintenanceException("database_restore_insufficient_space");

        var id = Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(profileRoot, RestoreStagePrefix + id);
        Directory.CreateDirectory(stageRoot);
        var stagedDatabase = Path.Combine(stageRoot, "restore.db");
        var stagedKey = Path.Combine(stageRoot, "restore-key.dat");
        var safetyBackup = Directory.Exists(profileRoot) && File.Exists(_options.DatabasePath)
            ? Path.Combine(profileRoot, PreRestorePrefix + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + id)
            : null;
        try
        {
            File.Copy(backup.DatabasePath, stagedDatabase, overwrite: false);
            if (backup.KeyPath is not null) File.Copy(backup.KeyPath, stagedKey, overwrite: false);
            if (safetyBackup is not null)
                await BackupAsync(safetyBackup, cancellationToken).ConfigureAwait(false);

            var journal = new RestoreJournal(
                1,
                "replacing",
                _options.DatabasePath,
                _keyPath,
                stageRoot,
                stagedDatabase,
                File.Exists(stagedKey) ? stagedKey : null,
                safetyBackup,
                DateTimeOffset.UtcNow);
            await WriteRestoreJournalAsync(profileRoot, journal, cancellationToken).ConfigureAwait(false);
            try
            {
                DeleteSqliteSidecars(_options.DatabasePath);
                InstallFile(stagedDatabase, _options.DatabasePath);
                _restoreStepObserver?.Invoke("database-installed");
                if (File.Exists(stagedKey)) InstallFile(stagedKey, _keyPath);
                else if (File.Exists(_keyPath)) File.Delete(_keyPath);
                _restoreStepObserver?.Invoke("key-installed");
                await ValidateDatabaseAsync(_options.DatabasePath, cancellationToken, requireCurrentVersion: true)
                    .ConfigureAwait(false);
                if (await HasProtectedRowsAsync(_options.DatabasePath, cancellationToken).ConfigureAwait(false))
                {
                    if (!File.Exists(_keyPath))
                        throw new DatabaseMaintenanceException("harness_key_missing");
                    var key = ReadOpaqueDpapiKey(_keyPath);
                    try { await ValidateProtectedDataKeyAsync(_options.DatabasePath, key, cancellationToken).ConfigureAwait(false); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
                }
                else if (File.Exists(_keyPath))
                {
                    var key = ReadOpaqueDpapiKey(_keyPath);
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                }
                await WriteRestoreJournalAsync(profileRoot, journal with { Phase = "complete" }, cancellationToken)
                    .ConfigureAwait(false);
                File.Delete(Path.Combine(profileRoot, RestoreJournalFileName));
                TryDeleteDirectory(stageRoot);
                return new DatabaseMaintenanceResult("restore", "complete", safetyBackup ?? _options.DatabasePath);
            }
            catch
            {
                await RecoverRestoreIfNeededAsync(_options.DatabasePath, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            TryDeleteDirectory(stageRoot);
            throw;
        }
    }

    public static async Task<bool> RecoverRestoreIfNeededAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var normalizedDatabase = Path.GetFullPath(databasePath);
        var profileRoot = Path.GetDirectoryName(normalizedDatabase)!;
        var journalPath = Path.Combine(profileRoot, RestoreJournalFileName);
        if (!File.Exists(journalPath)) return false;

        RestoreJournal? journal;
        try
        {
            var info = new FileInfo(journalPath);
            if (info.Length > 64 * 1024) throw new InvalidDataException();
            journal = JsonSerializer.Deserialize<RestoreJournal>(
                await File.ReadAllTextAsync(journalPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new DatabaseMaintenanceException("database_restore_journal_invalid", exception);
        }

        if (journal is null || journal.Version != 1
            || journal.Phase is not ("replacing" or "complete")
            || string.IsNullOrWhiteSpace(journal.DatabasePath)
            || string.IsNullOrWhiteSpace(journal.KeyPath)
            || string.IsNullOrWhiteSpace(journal.StageRoot)
            || string.IsNullOrWhiteSpace(journal.StagedDatabasePath)
            || !PathEquals(journal.DatabasePath, normalizedDatabase)
            || !PathEquals(journal.KeyPath, Path.Combine(profileRoot, "harness_key.dat"))
            || !IsOwnedChild(profileRoot, journal.StageRoot, RestoreStagePrefix)
            || !IsChild(journal.StageRoot, journal.StagedDatabasePath)
            || (journal.StagedKeyPath is not null && !IsChild(journal.StageRoot, journal.StagedKeyPath))
            || (journal.SafetyBackupPath is not null && !IsOwnedChild(profileRoot, journal.SafetyBackupPath, PreRestorePrefix)))
            throw new DatabaseMaintenanceException("database_restore_journal_invalid");

        // These are fixed sibling staging files created only by InstallFile. The
        // validated journal binds their authoritative paths to this profile.
        foreach (var temporary in new[] { normalizedDatabase + ".restore-new", journal.KeyPath + ".restore-new" })
            if (File.Exists(temporary)) File.Delete(temporary);

        if (journal.Phase == "complete")
        {
            File.Delete(journalPath);
            TryDeleteDirectory(journal.StageRoot);
            return true;
        }

        var rollbackDb = journal.SafetyBackupPath is null
            ? null
            : Path.Combine(journal.SafetyBackupPath, "agent_harness.db");
        var rollbackKey = journal.SafetyBackupPath is null
            ? null
            : Path.Combine(journal.SafetyBackupPath, "harness_key.dat");
        if (journal.SafetyBackupPath is not null)
        {
            var validator = new DatabaseMaintenanceService(
                new HarnessSqliteOptions(Path.Combine(journal.SafetyBackupPath, "agent_harness.db")),
                Path.Combine(journal.SafetyBackupPath, "harness_key.dat"));
            await validator.ValidateBackupAsync(journal.SafetyBackupPath, cancellationToken).ConfigureAwait(false);
        }

        DeleteSqliteSidecars(normalizedDatabase);
        if (File.Exists(normalizedDatabase)) File.Delete(normalizedDatabase);
        if (File.Exists(journal.KeyPath)) File.Delete(journal.KeyPath);
        if (rollbackDb is not null && File.Exists(rollbackDb)) File.Copy(rollbackDb, normalizedDatabase, overwrite: true);
        if (rollbackKey is not null && File.Exists(rollbackKey)) File.Copy(rollbackKey, journal.KeyPath, overwrite: true);
        File.Delete(journalPath);
        TryDeleteDirectory(journal.StageRoot);
        return true;
    }

    private static async Task WriteRestoreJournalAsync(string profileRoot, RestoreJournal journal, CancellationToken cancellationToken)
    {
        var target = Path.Combine(profileRoot, RestoreJournalFileName);
        var temporary = target + ".tmp";
        var content = JsonSerializer.Serialize(journal, JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, target, overwrite: true);
    }

    private static void InstallFile(string stagedPath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + ".restore-new";
        File.Copy(stagedPath, temporary, overwrite: true);
        using (var staged = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            staged.Flush(flushToDisk: true);
        File.Move(temporary, destinationPath, overwrite: true);
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); }
            catch (IOException exception) { throw new DatabaseMaintenanceException("database_sidecar_remove_failed", exception); }
            catch (UnauthorizedAccessException exception) { throw new DatabaseMaintenanceException("database_sidecar_remove_failed", exception); }
        }
    }

    private static bool IsOwnedChild(string root, string path, string requiredPrefix)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        return parent is not null && PathEquals(parent, root)
            && Path.GetFileName(fullPath).StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChild(string root, string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) is { } parent && PathEquals(parent, root);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private sealed record RestoreJournal(
        int Version,
        string Phase,
        string DatabasePath,
        string KeyPath,
        string StageRoot,
        string StagedDatabasePath,
        string? StagedKeyPath,
        string? SafetyBackupPath,
        DateTimeOffset StartedAtUtc);
}
