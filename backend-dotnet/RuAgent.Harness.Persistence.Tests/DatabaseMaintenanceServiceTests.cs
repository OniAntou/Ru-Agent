using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Maintenance;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class DatabaseMaintenanceServiceTests
{
    [Fact]
    public async Task Backup_uses_sqlite_snapshot_while_wal_writer_is_open_and_verify_accepts_it()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var destination = Path.Combine(fixture.RootPath, "backups", "snapshot");
        var maintenance = new DatabaseMaintenanceService(
            fixture.Options,
            Path.Combine(fixture.RootPath, "harness_key.dat"));

        await using var writer = fixture.CreateRawConnection();
        await writer.OpenAsync();
        await using (var command = writer.CreateCommand())
        {
            command.CommandText = "INSERT INTO business_settings(id, document) VALUES (1, '{}');";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(File.Exists(fixture.Options.DatabasePath + "-wal"));

        var result = await maintenance.BackupAsync(destination, CancellationToken.None);

        Assert.Equal("created", result.Status);
        Assert.True(File.Exists(Path.Combine(destination, "agent_harness.db")));
        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
        var verify = await maintenance.VerifyAsync(destination, CancellationToken.None);
        Assert.Equal("valid", verify.Status);
    }

    [Fact]
    public async Task Verify_rejects_tampered_snapshot_file_by_digest()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var destination = Path.Combine(fixture.RootPath, "backups", "snapshot");
        var maintenance = new DatabaseMaintenanceService(
            fixture.Options,
            Path.Combine(fixture.RootPath, "harness_key.dat"));
        await maintenance.BackupAsync(destination, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(destination, "agent_harness.db"), "tamper");

        var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.VerifyAsync(destination, CancellationToken.None));

        Assert.Equal("database_backup_hash_mismatch", error.ErrorCode);
    }

    [Fact]
    public async Task Backup_refuses_to_replace_existing_destination()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var destination = Path.Combine(fixture.RootPath, "backups", "snapshot");
        Directory.CreateDirectory(destination);
        var maintenance = new DatabaseMaintenanceService(
            fixture.Options,
            Path.Combine(fixture.RootPath, "harness_key.dat"));

        var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.BackupAsync(destination, CancellationToken.None));

        Assert.Equal("database_backup_destination_exists", error.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task Backup_and_compact_fail_with_named_space_errors_before_mutating_data()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var destination = Path.Combine(fixture.RootPath, "backups", "snapshot");
        var maintenance = new DatabaseMaintenanceService(
            fixture.Options,
            Path.Combine(fixture.RootPath, "harness_key.dat"),
            restoreStepObserver: null,
            availableSpaceProvider: _ => 0);

        var backupError = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.BackupAsync(destination, CancellationToken.None));
        var compactError = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.CompactAsync(CancellationToken.None));

        Assert.Equal("database_backup_insufficient_space", backupError.ErrorCode);
        Assert.Equal("database_compact_insufficient_space", compactError.ErrorCode);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Verify_rejects_missing_or_malformed_key_when_protected_rows_exist()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_payloads(
                    payload_id, run_id, kind, audience, encoding, byte_cap, protected_content,
                    protection_version, nonce, authentication_tag, created_at_utc)
                VALUES ('payload_00000000000000000000000000000001', $run, 0, 0, 0, 100, X'01', 1,
                    zeroblob(12), zeroblob(16), '2026-10-01T00:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$run", run.Run.RunId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var destination = Path.Combine(fixture.RootPath, "backups", "missing-key");
        var maintenance = new DatabaseMaintenanceService(
            fixture.Options,
            Path.Combine(fixture.RootPath, "harness_key.dat"));
        await maintenance.BackupAsync(destination, CancellationToken.None);
        var missing = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.VerifyAsync(destination, CancellationToken.None));
        Assert.Equal("harness_key_missing", missing.ErrorCode);

        var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
        await File.WriteAllBytesAsync(keyPath, [1, 2, 3, 4]);
        var malformedDestination = Path.Combine(fixture.RootPath, "backups", "wrong-key");
        await maintenance.BackupAsync(malformedDestination, CancellationToken.None);
        var malformed = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.VerifyAsync(malformedDestination, CancellationToken.None));
        Assert.Equal("harness_key_invalid", malformed.ErrorCode);
    }

    [Fact]
    public async Task Restore_rolls_back_database_and_key_if_pair_replacement_fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
        var originalKey = CreateCurrentUserKeyFile();
        await File.WriteAllBytesAsync(keyPath, originalKey);
        await SetBusinessSettingAsync(fixture.Options.DatabasePath, """{"value":"incoming"}""");
        var incomingPath = Path.Combine(fixture.RootPath, "backups", "incoming");
        var normal = new DatabaseMaintenanceService(fixture.Options, keyPath);
        await normal.BackupAsync(incomingPath, CancellationToken.None);
        await SetBusinessSettingAsync(fixture.Options.DatabasePath, """{"value":"current"}""");
        ClearProfilePool(fixture.Options);

        var failing = new DatabaseMaintenanceService(fixture.Options, keyPath, step =>
        {
            if (step == "database-installed") throw new IOException("injected replacement failure");
        });
        await Assert.ThrowsAsync<IOException>(() => failing.RestoreAsync(incomingPath, CancellationToken.None));

        Assert.Equal("""{"value":"current"}""", await ReadBusinessSettingAsync(fixture.Options.DatabasePath));
        Assert.Equal(originalKey, await File.ReadAllBytesAsync(keyPath));
    }

    [Fact]
    public async Task Verify_rejects_a_different_valid_current_user_key()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
        await File.WriteAllBytesAsync(keyPath, ProtectCurrentUserKey(correctKey));
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);
        var runId = run.Run.RunId;
        var payloadId = HarnessStoreTestFixture.Payload(1);
        var intent = new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            64,
            "key validation"u8.ToArray());
        var crypto = new HarnessPayloadCryptography(new FixedTestKeyStore(correctKey));
        var encrypted = await crypto.EncryptAsync(runId, intent, CancellationToken.None);
        await InsertPayloadAsync(fixture.Options.DatabasePath, runId, intent, encrypted);

        var destination = Path.Combine(fixture.RootPath, "backups", "snapshot");
        var maintenance = new DatabaseMaintenanceService(fixture.Options, keyPath);
        await maintenance.BackupAsync(destination, CancellationToken.None);

        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var backupKeyPath = Path.Combine(destination, "harness_key.dat");
        var wrongKeyFile = ProtectCurrentUserKey(wrongKey);
        await File.WriteAllBytesAsync(backupKeyPath, wrongKeyFile);
        var manifestPath = Path.Combine(destination, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["keySha256"] = Convert.ToHexString(SHA256.HashData(wrongKeyFile)).ToLowerInvariant();
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => maintenance.VerifyAsync(destination, CancellationToken.None));

        Assert.Equal("database_key_mismatch", error.ErrorCode);
        CryptographicOperations.ZeroMemory(correctKey);
        CryptographicOperations.ZeroMemory(wrongKey);
    }

    [Fact]
    public void Owner_lease_rejects_another_host_for_the_same_profile_and_releases_with_process_owner()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "agent_harness.db");
        var root = Path.GetDirectoryName(databasePath)!;
        using (DatabaseOwnerLease.Acquire(databasePath))
        {
            var error = Assert.Throws<DatabaseMaintenanceException>(() =>
                DatabaseOwnerLease.Acquire(databasePath));
            Assert.Equal("database_profile_in_use", error.ErrorCode);
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Pending_restore_journal_recovers_the_pre_restore_snapshot_before_database_use()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await SetBusinessSettingAsync(fixture.Options.DatabasePath, """{"value":"before"}""");
        var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
        var maintenance = new DatabaseMaintenanceService(fixture.Options, keyPath);
        var safetyBackup = Path.Combine(fixture.RootPath, "database-maintenance-pre-restore-test");
        await maintenance.BackupAsync(safetyBackup, CancellationToken.None);
        await SetBusinessSettingAsync(fixture.Options.DatabasePath, """{"value":"partial"}""");

        var stageRoot = Path.Combine(fixture.RootPath, ".ru-database-restore-test");
        Directory.CreateDirectory(stageRoot);
        var stagedDatabase = Path.Combine(stageRoot, "restore.db");
        await File.WriteAllTextAsync(stagedDatabase, "not installed");
        var journal = JsonSerializer.Serialize(new
        {
            version = 1,
            phase = "replacing",
            databasePath = fixture.Options.DatabasePath,
            keyPath,
            stageRoot,
            stagedDatabasePath = stagedDatabase,
            stagedKeyPath = (string?)null,
            safetyBackupPath = safetyBackup,
            startedAtUtc = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "database-restore-journal.json"), journal);

        using var owner = DatabaseOwnerLease.Acquire(fixture.Options.DatabasePath);
        ClearProfilePool(fixture.Options);
        var recovered = await DatabaseMaintenanceService.RecoverRestoreIfNeededAsync(
            fixture.Options.DatabasePath,
            CancellationToken.None);

        Assert.True(recovered);
        Assert.Equal("""{"value":"before"}""", await ReadBusinessSettingAsync(fixture.Options.DatabasePath));
        Assert.False(File.Exists(Path.Combine(fixture.RootPath, "database-restore-journal.json")));
        Assert.False(Directory.Exists(stageRoot));
    }

    private static byte[] CreateCurrentUserKeyFile()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            return ProtectCurrentUserKey(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] ProtectCurrentUserKey(byte[] key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var protectedKey = ProtectedData.Protect(key, WindowsDpapiKeyStore.DpapiEntropy, DataProtectionScope.CurrentUser);
        return [.. "RUHK1"u8.ToArray(), .. protectedKey];
    }

    private static async Task InsertPayloadAsync(
        string databasePath,
        RunId runId,
        HarnessPayloadIntent intent,
        EncryptedHarnessPayload encrypted)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_payloads(
                payload_id, run_id, kind, audience, encoding, byte_cap, protected_content,
                protection_version, nonce, authentication_tag, created_at_utc)
            VALUES ($payload, $run, $kind, $audience, $encoding, $cap, $content,
                $protection, $nonce, $tag, '2026-10-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$payload", intent.PayloadId.Value);
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$kind", (int)intent.Kind);
        command.Parameters.AddWithValue("$audience", (int)intent.Audience);
        command.Parameters.AddWithValue("$encoding", (int)intent.Encoding);
        command.Parameters.AddWithValue("$cap", intent.ByteCap);
        command.Parameters.AddWithValue("$content", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$protection", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetBusinessSettingAsync(string databasePath, string document)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO business_settings(id, document) VALUES (1, $document) ON CONFLICT(id) DO UPDATE SET document = excluded.document;";
        command.Parameters.AddWithValue("$document", document);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadBusinessSettingAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM business_settings WHERE id = 1;";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static void ClearProfilePool(HarnessSqliteOptions options)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = options.DefaultTimeoutSeconds,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private sealed class FixedTestKeyStore(byte[] key) : IHarnessKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((byte[])key.Clone());
        }
    }
}
