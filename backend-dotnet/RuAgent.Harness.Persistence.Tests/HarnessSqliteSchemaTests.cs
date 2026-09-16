using Microsoft.Data.Sqlite;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessSqliteSchemaTests
{
    private static readonly string[] RequiredTables =
    [
        "agent_runs",
        "agent_events",
        "agent_payloads",
        "agent_conversation_outbox",
        "agent_global_rules",
        "agent_project_rules",
        "agent_run_rules",
        "agent_workspaces",
        "agent_session_settings",
        "agent_context_compactions",
        "workspace_coordinators",
        "workspace_run_members",
        "workspace_path_claims",
        "workspace_file_snapshots",
        "workspace_change_journal",
        "agent_file_recovery",
    ];

    private static readonly string[] RequiredIndexes =
    [
        "ux_agent_runs_active_session",
        "ux_agent_payloads_context_source",
        "ux_agent_payloads_context_sequence",
        "ix_agent_conversation_outbox_dispatch",
        "ix_agent_context_compactions_run_created",
        "ix_workspace_path_claims_active",
        "ix_workspace_change_journal_sequence",
    ];

    [Fact]
    public async Task Initialize_creates_wal_schema_and_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        var second = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.True(second.IsAvailable);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND (name LIKE 'agent_%' OR name LIKE 'workspace_%')";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredTables, name => Assert.Contains(name, names));
        // workspace_mutations was never read or written by production code; a
        // fresh install must not create the table it was dropped from.
        Assert.DoesNotContain("workspace_mutations", names);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND (name LIKE '%agent_%' OR name LIKE '%workspace_%')";
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await indexCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredIndexes, name => Assert.Contains(name, indexes));
        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await journalCommand.ExecuteScalarAsync());
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        Assert.Equal(
            HarnessSqliteSchema.CurrentVersion,
            (long)(await versionCommand.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Migrating_from_six_adds_file_recovery_and_drops_the_dead_mutations_table()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        // Reproduce the pre-v7 shape: the lazily created recovery table is absent
        // and the never-used workspace_mutations table is still present.
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DROP TABLE IF EXISTS agent_file_recovery;
                CREATE TABLE IF NOT EXISTS workspace_mutations (
                    mutation_id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    state TEXT NOT NULL
                );
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        Assert.True(await TableExistsAsync(migrated, "agent_file_recovery"));
        Assert.False(await TableExistsAsync(migrated, "workspace_mutations"));
    }

    [Fact]
    public async Task Migrating_from_six_preserves_recovery_rows_created_before_the_migration()
    {
        // A database that already wrote recovery copies through the old lazy
        // creation must keep them: the migration is CREATE IF NOT EXISTS, not a
        // rebuild.
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DROP TABLE IF EXISTS agent_file_recovery;
                CREATE TABLE agent_file_recovery (
                    recovery_id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    canonical_path TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    content_length INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    consumed_at_utc TEXT NULL
                );
                INSERT INTO agent_file_recovery
                    (recovery_id, run_id, canonical_path, content_sha256, content_length, created_at_utc, consumed_at_utc)
                VALUES ('rec_1', 'run_1', 'C:\ws\a.txt', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 3, '2026-01-01T00:00:00Z', NULL);
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        await using var count = migrated.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM agent_file_recovery WHERE recovery_id = 'rec_1'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? 0L));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) > 0;
    }

    [Fact]
    public async Task Initialize_reports_unavailable_without_throwing_for_an_invalid_database_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-harness-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new HarnessSqliteOptions(root);

            var health = await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);

            Assert.False(health.IsAvailable);
            Assert.Equal("harness_store_unavailable", health.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_creates_workspace_coordinator_tables_with_required_columns()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(workspace_coordinators)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("workspace_id", columns);
        Assert.Contains("mode", columns);
        Assert.Contains("commit_sequence", columns);
        Assert.Contains("policy_json", columns);
    }

    [Fact]
    public async Task Initialize_rejects_a_legacy_version_one_database_without_reinterpreting_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-harness-invalid-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE agent_runs(run_id TEXT PRIMARY KEY);
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var health = await HarnessSqliteSchema.InitializeAsync(
                new HarnessSqliteOptions(databasePath),
                CancellationToken.None);

            Assert.False(health.IsAvailable);
            Assert.Equal("harness_schema_incompatible", health.ErrorCode);
        }
        finally
        {
            using (var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString()))
            {
                SqliteConnection.ClearPool(pool);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_migrates_version_two_additively_and_preserves_existing_rows()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-schema-migration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var options = new HarnessSqliteOptions(databasePath);
            var initial = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);
            Assert.True(initial.IsAvailable, initial.ErrorCode);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                    VALUES ('migration-sentinel', 0, 7, '2026-08-29T00:00:00.0000000+00:00');
                    DROP INDEX IF EXISTS ix_agent_context_compactions_run_created;
                    DROP TABLE IF EXISTS agent_context_compactions;
                    PRAGMA user_version = 2;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var migrated = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);

            Assert.True(migrated.IsAvailable, migrated.ErrorCode);
            Assert.Equal(HarnessSqliteSchema.CurrentVersion, migrated.SchemaVersion);
            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync();
            await using var sentinel = verify.CreateCommand();
            sentinel.CommandText =
                "SELECT version FROM agent_session_settings WHERE session_id = 'migration-sentinel'";
            Assert.Equal(7L, (long)(await sentinel.ExecuteScalarAsync() ?? 0L));
            await using var table = verify.CreateCommand();
            table.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'agent_context_compactions'";
            Assert.Equal(1L, (long)(await table.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            using (var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString()))
            {
                SqliteConnection.ClearPool(pool);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Initialize_migrates_version_five_to_six_preserving_rows_and_widening_preset()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-schema-migration-v6",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var options = new HarnessSqliteOptions(databasePath);
            var initial = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);
            Assert.True(initial.IsAvailable, initial.ErrorCode);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO agent_workspaces(workspace_id, canonical_root_nonce, canonical_root_ciphertext, canonical_root_tag, volume_serial_number, root_file_id, display_identity_hash, trust_request_id, trust_requested_at_utc, trust_expires_at_utc, created_at_utc, updated_at_utc)
                    VALUES ('ws_55555555555555555555555555555555', zeroblob(12), zeroblob(1), zeroblob(16), 'VOL', 'fid', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'trust-1', '2026-09-01T00:00:00+00:00', '2026-09-02T00:00:00+00:00', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00');
                    INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                    VALUES ('migration-sentinel', 0, 7, '2026-08-29T00:00:00.0000000+00:00');
                    INSERT INTO agent_runs(run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count)
                    VALUES ('run_66666666666666666666666666666666', 'migration-sentinel', 'ws_55555555555555555555555555555555', 7, NULL, 0, 3, 1, 0, 0, 0, 1, 1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00', NULL, NULL, NULL, 0);
                    PRAGMA user_version = 5;
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var migrated = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);

            Assert.True(migrated.IsAvailable, migrated.ErrorCode);
            Assert.Equal(HarnessSqliteSchema.CurrentVersion, migrated.SchemaVersion);
            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync();
            await using var sentinel = verify.CreateCommand();
            sentinel.CommandText =
                "SELECT version FROM agent_session_settings WHERE session_id = 'migration-sentinel'";
            Assert.Equal(7L, (long)(await sentinel.ExecuteScalarAsync() ?? 0L));
            await using var run = verify.CreateCommand();
            run.CommandText =
                "SELECT last_sequence FROM agent_runs WHERE run_id = 'run_66666666666666666666666666666666'";
            Assert.Equal(3L, (long)(await run.ExecuteScalarAsync() ?? -1L));
            // The widened CHECK now accepts the Trusted preset on both tables.
            await using var trusted = verify.CreateCommand();
            trusted.CommandText =
                "INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc) VALUES ('trusted-sentinel', 2, 1, '2026-09-01T00:00:00+00:00')";
            await trusted.ExecuteNonQueryAsync();
            await using var trustedRun = verify.CreateCommand();
            trustedRun.CommandText =
                "UPDATE agent_runs SET preset = 2 WHERE run_id = 'run_66666666666666666666666666666666'";
            await trustedRun.ExecuteNonQueryAsync();
        }
        finally
        {
            using (var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString()))
            {
                SqliteConnection.ClearPool(pool);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
