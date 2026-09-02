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
        "workspace_mutations",
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
}
