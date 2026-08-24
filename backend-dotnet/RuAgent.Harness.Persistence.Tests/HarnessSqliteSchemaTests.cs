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
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'agent_%'";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredTables, name => Assert.Contains(name, names));
        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await journalCommand.ExecuteScalarAsync());
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, (long)(await versionCommand.ExecuteScalarAsync() ?? 0L));
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
    public async Task Initialize_rejects_a_version_one_database_with_an_invalid_shape()
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
            Assert.Equal("harness_schema_invalid", health.ErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
