using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessSqliteSchemaTests
{
    private static readonly string[] RequiredTables =
    [
        "business_migrations",
        "business_settings",
        "business_sessions",
        "business_activity",
        "agent_runs",
        "agent_run_provider_selections",
        "agent_events",
        "agent_payloads",
        "agent_conversation_outbox",
        "agent_global_rules",
        "agent_project_rules",
        "agent_run_rules",
        "agent_workspaces",
        "agent_session_settings",
        "agent_session_tombstones",
        "agent_session_preset_snapshots",
        "agent_context_compactions",
        "workspace_coordinators",
        "workspace_run_members",
        "workspace_path_claims",
        "workspace_change_journal",
        "agent_file_recovery",
    ];

    private static readonly string[] RequiredIndexes =
    [
        "business_sessions_updated",
        "business_activity_timestamp",
        "ux_agent_runs_active_session",
        "ix_agent_runs_session_created_run",
        "ux_agent_payloads_context_source",
        "ux_agent_payloads_context_sequence",
        "ix_agent_conversation_outbox_dispatch",
        "ix_agent_context_compactions_run_created",
        "ix_workspace_path_claims_active",
        "ix_workspace_change_journal_sequence",
        "ix_agent_session_preset_snapshots_expiry",
        "ix_agent_session_preset_snapshots_session",
        "ix_agent_events_provider_continuation",
        "ix_agent_events_session_receipts",
    ];

    [Fact]
    public async Task Initialize_creates_wal_schema_and_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        var second = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.True(second.IsAvailable);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, second.SchemaVersion);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND (name LIKE 'agent_%' OR name LIKE 'workspace_%' OR name LIKE 'business_%')";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredTables, name => Assert.Contains(name, names));
        // workspace_mutations and workspace_file_snapshots were never read or
        // written by production code; a fresh install must not create the tables
        // they were dropped from.
        Assert.DoesNotContain("workspace_mutations", names);
        Assert.DoesNotContain("workspace_file_snapshots", names);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND (name LIKE '%agent_%' OR name LIKE '%workspace_%' OR name LIKE 'business_%')";
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
    public async Task Schema_persists_typed_run_model_context_metadata()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_run_provider_selections)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        Assert.Contains("model_label", columns);
        Assert.Contains("calling_mode", columns);
        Assert.Contains("context_window_tokens", columns);
        Assert.Contains("context_window_source", columns);
        Assert.Contains("context_window_version", columns);
        Assert.Contains("output_token_reserve", columns);
    }

    [Fact]
    public async Task Initialize_adds_durable_session_preset_snapshots_without_losing_existing_data()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, permission_profile_version, version, updated_at_utc)
                VALUES ('session-snapshot-sentinel', 'standard', 1, 4, '2026-08-24T01:02:03.0000000+00:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT preset, permission_profile_version, version FROM agent_session_settings WHERE session_id = 'session-snapshot-sentinel'";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("standard", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(4L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Migration_backup_preserves_snapshot_schema_and_existing_rows()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, permission_profile_version, version, updated_at_utc)
                VALUES ('session-backup-sentinel', 'plan', 1, 3, '2026-08-24T01:02:03.0000000+00:00');
                """;
            await seed.ExecuteNonQueryAsync();
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var backupPath = Path.Combine(fixture.RootPath, "agent_harness.backup.db");
        File.Copy(fixture.Options.DatabasePath, backupPath);
        var backupOptions = new HarnessSqliteOptions(backupPath);
        var health = await HarnessSqliteSchema.InitializeAsync(backupOptions, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backup.OpenAsync();
            await using var read = backup.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM agent_session_preset_snapshots";
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync() ?? 0L));
            read.CommandText = "SELECT preset, version FROM agent_session_settings WHERE session_id = 'session-backup-sentinel'";
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("plan", reader.GetString(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }

        SqliteConnection.ClearAllPools();
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
                    DROP TABLE IF EXISTS agent_session_work_states;
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
            // All pools, not one string: pools are keyed by the full connection
            // string, and these tests open the same file through several
            // different option sets.
            SqliteConnection.ClearAllPools();

            Directory.Delete(root, recursive: true);
        }
    }
}
