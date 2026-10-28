using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Maintenance;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class DatabaseMaintenanceValidationTests
{
    [Fact]
    public async Task Validation_rejects_current_version_database_with_missing_schema()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-database-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "invalid.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA user_version={HarnessSqliteSchema.CurrentVersion};";
                await command.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
                () => DatabaseMaintenanceService.ValidateDatabaseAsync(path, CancellationToken.None, requireCurrentVersion: true));

            Assert.Equal("database_schema_invalid", error.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_rejects_foreign_key_violations_that_quick_check_does_not_report()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                INSERT INTO business_session_messages(session_id,message_index,role,content)
                VALUES('orphan-session',0,'user','orphan');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => DatabaseMaintenanceService.ValidateDatabaseAsync(
                fixture.Options.DatabasePath, CancellationToken.None, requireCurrentVersion: true));

        Assert.Equal("database_foreign_key_integrity_failed", error.ErrorCode);
    }

    [Fact]
    public async Task Validation_rejects_external_content_fts_index_drift()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var sessionId = HarnessStoreTestFixture.Session(1).Value;
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO business_sessions(session_id,document,summary,updated_at_utc)
                VALUES($session,'{}','{}','2026-10-26T00:00:00.0000000+00:00');
                INSERT INTO business_session_messages(session_id,message_index,role,content)
                VALUES($session,0,'user','searchable');
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            await command.ExecuteNonQueryAsync();
        }
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO business_session_message_search(
                    business_session_message_search,rowid,session_id,message_index,role,content)
                SELECT 'delete',message_id,session_id,message_index,role,content
                FROM business_session_messages;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
            () => DatabaseMaintenanceService.ValidateDatabaseAsync(
                fixture.Options.DatabasePath, CancellationToken.None, requireCurrentVersion: true));

        Assert.Equal("database_search_integrity_failed", error.ErrorCode);
    }

    [Fact]
    public async Task Verify_rejects_corrupt_work_state_ciphertext()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var correctKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var workspace = await fixture.RegisterWorkspaceAsync(1);
            await SetWorkspaceRootKeyAsync(fixture, workspace.WorkspaceId, correctKey);
            var sessionId = HarnessStoreTestFixture.Session(1);
            var runId = HarnessStoreTestFixture.Run(1);
            var eventId = HarnessStoreTestFixture.Event(1);
            var fact = new SessionWorkFact("preserve goal", runId.Value, eventId.Value);
            var state = new SessionWorkState(sessionId, workspace.WorkspaceId,
                workspace.DisplayIdentityHash, runId, 1, eventId.Value, fact, [], [], false, false,
                [], [], [], null, null);
            var crypto = new SessionWorkStateCryptography(new FixedTestKeyStore(correctKey));
            var encrypted = await crypto.ProtectAsync(state, CancellationToken.None);
            await using (var connection = fixture.CreateRawConnection())
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO agent_session_work_states(
                        session_id,workspace_id,workspace_identity_sha256,source_run_id,source_event_id,version,
                        protection_version,nonce,ciphertext,authentication_tag,updated_at_utc)
                    VALUES($session,$workspace,$hash,$run,$event,1,$protection,$nonce,$ciphertext,$tag,
                        '2026-10-26T00:00:00.0000000+00:00');
                    """;
                command.Parameters.AddWithValue("$session", sessionId.Value);
                command.Parameters.AddWithValue("$workspace", workspace.WorkspaceId.Value);
                command.Parameters.AddWithValue("$hash", workspace.DisplayIdentityHash);
                command.Parameters.AddWithValue("$run", runId.Value);
                command.Parameters.AddWithValue("$event", eventId.Value);
                command.Parameters.AddWithValue("$protection", encrypted.ProtectionVersion);
                command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
                command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
                command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
                await command.ExecuteNonQueryAsync();
            }

            var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
            await File.WriteAllBytesAsync(keyPath, ProtectCurrentUserKey(correctKey));
            var backup = Path.Combine(fixture.RootPath, "backups", "work-state");
            var maintenance = new DatabaseMaintenanceService(fixture.Options, keyPath);
            await maintenance.BackupAsync(backup, CancellationToken.None);
            await CorruptWorkStateCiphertextAsync(backup);

            var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
                () => maintenance.VerifyAsync(backup, CancellationToken.None));

            Assert.Equal("database_key_mismatch", error.ErrorCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(correctKey);
        }
    }

    [Fact]
    public async Task Verify_rejects_corrupt_scope_after_relation_cleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var correctKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var workspace = await fixture.RegisterWorkspaceAsync(1);
            await SetWorkspaceRootKeyAsync(fixture, workspace.WorkspaceId, correctKey);
            var rootRun = fixture.NewRun(1, 1, 1);
            var childRun = fixture.NewRun(2, 2, 1);
            var eventStore = fixture.CreateEventStore();
            Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(rootRun, CancellationToken.None)).Outcome);
            Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(childRun, CancellationToken.None)).Outcome);
            var scope = "{}"u8.ToArray();
            var encrypted = await new WorkspaceJournalProtector(new FixedTestKeyStore(correctKey)).EncryptAsync(
                workspace.WorkspaceId, childRun.Run.RunId.Value, "subagent.scope", scope, CancellationToken.None);
            await using (var connection = fixture.CreateRawConnection())
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO agent_subagent_families(root_run_id,canceled) VALUES($root,0);
                    INSERT INTO agent_subagents(agent_id,root_run_id,internal_session_id,role,canceled,invocation_id,request_hash)
                    VALUES('agent_test',$root,$session,0,0,'invocation_test',$hash);
                    INSERT INTO agent_subagent_attempts(agent_id,attempt_number,run_id,created_at_utc)
                    VALUES('agent_test',1,$child,'2026-10-26T00:00:00.0000000+00:00');
                    INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($child,$root);
                    INSERT INTO agent_subagent_scopes(run_id,protection_version,nonce,ciphertext,authentication_tag)
                    VALUES($child,$protection,$nonce,$ciphertext,$tag);
                    """;
                command.Parameters.AddWithValue("$root", rootRun.Run.RunId.Value);
                command.Parameters.AddWithValue("$session", HarnessStoreTestFixture.Session(3).Value);
                command.Parameters.AddWithValue("$child", childRun.Run.RunId.Value);
                command.Parameters.AddWithValue("$hash", HarnessStoreTestFixture.Sha('a'));
                command.Parameters.AddWithValue("$protection", encrypted.ProtectionVersion);
                command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
                command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
                command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
                await command.ExecuteNonQueryAsync();
            }

            var keyPath = Path.Combine(fixture.RootPath, "harness_key.dat");
            await File.WriteAllBytesAsync(keyPath, ProtectCurrentUserKey(correctKey));
            var backup = Path.Combine(fixture.RootPath, "backups", "subagent-scope");
            var maintenance = new DatabaseMaintenanceService(fixture.Options, keyPath);
            await maintenance.BackupAsync(backup, CancellationToken.None);
            await CorruptOrphanedScopeAsync(backup, childRun.Run.RunId.Value);

            var error = await Assert.ThrowsAsync<DatabaseMaintenanceException>(
                () => maintenance.VerifyAsync(backup, CancellationToken.None));

            Assert.Equal("database_key_mismatch", error.ErrorCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(correctKey);
        }
    }

    private static async Task CorruptWorkStateCiphertextAsync(string backup)
    {
        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(backup, "agent_harness.db")};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_session_work_states SET ciphertext=zeroblob(length(ciphertext));";
            await command.ExecuteNonQueryAsync();
        }
        await RefreshDatabaseManifestHashAsync(backup);
    }

    private static async Task CorruptOrphanedScopeAsync(string backup, string childRunId)
    {
        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(backup, "agent_harness.db")};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_subagent_scopes SET authentication_tag=zeroblob(length(authentication_tag))
                WHERE run_id=$child;
                DELETE FROM agent_subagent_attempts WHERE run_id=$child;
                DELETE FROM agent_subagents WHERE agent_id='agent_test';
                """;
            command.Parameters.AddWithValue("$child", childRunId);
            await command.ExecuteNonQueryAsync();
        }
        await RefreshDatabaseManifestHashAsync(backup);
    }

    private static async Task RefreshDatabaseManifestHashAsync(string backup)
    {
        var dbPath = Path.Combine(backup, "agent_harness.db");
        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        await using var stream = File.OpenRead(dbPath);
        manifest["databaseSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }

    private static async Task SetWorkspaceRootKeyAsync(
        HarnessStoreTestFixture fixture,
        WorkspaceId workspaceId,
        byte[] key)
    {
        var encrypted = WorkspaceRootCryptography.Encrypt(workspaceId, key, "C:\\ru-test-workspace"u8);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_workspaces SET canonical_root_nonce=$nonce,
                canonical_root_ciphertext=$ciphertext,canonical_root_tag=$tag
            WHERE workspace_id=$workspace;
            """;
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        command.Parameters.AddWithValue("$workspace", workspaceId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static byte[] ProtectCurrentUserKey(byte[] key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return [.. Encoding.ASCII.GetBytes("RUHK1"), .. ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)];
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
