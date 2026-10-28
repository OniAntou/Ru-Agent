using System.Diagnostics;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ReleasedSchemaMigrationTests
{
    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    public async Task Retained_development_source_upgrades_without_losing_release_data(int sourceVersion)
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
            "RuAgent.Harness.Persistence.Tests.Fixtures.development-migrations.json")
            ?? throw new InvalidOperationException("Development migration fixture is missing.");
        var fragments = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<int, string>>(resource)
            ?? throw new InvalidOperationException("Invalid development fixture.");
        // Frozen pre-refactor DDL applied to the actual released baseline;
        // never relabel a database created by the current production schema.
        var originalDdl = string.Join('\n', Enumerable.Range(18, sourceVersion - 18).Select(version => fragments[version]));
        // The original migrator disables FKs outside the batch when rebuilding
        // a parent table. Apply the snapshot under that same connection setting.
        await fixture.ExecuteAsync("PRAGMA foreign_keys=OFF;\n" + originalDdl
            + $"\nPRAGMA user_version={sourceVersion}; PRAGMA foreign_keys=ON;");
        Assert.Equal("DEADBEEF", await fixture.ScalarAsync("SELECT hex(protected_content) FROM agent_payloads"));

        var result = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.True(result.IsAvailable, result.ErrorCode);
        await AssertRecordsPreservedAsync(fixture);
        await fixture.ExecuteAsync("PRAGMA foreign_keys=ON; DELETE FROM agent_runs;");
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM agent_events"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM agent_payloads"));
    }

    [Fact]
    public async Task V29_to_v30_normalizes_history_and_preserves_pruned_source_receipts()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
            "RuAgent.Harness.Persistence.Tests.Fixtures.development-migrations.json")!;
        var fragments = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<int, string>>(resource)!;
        var v29 = string.Join('\n', Enumerable.Range(18, 10).Select(version => fragments![version]));
        await fixture.ExecuteAsync(v29 + "\nPRAGMA user_version=29;");
        await fixture.ExecuteAsync("""
            UPDATE business_sessions
            SET document = CAST('{"id":"released-session","title":"Legacy","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","messages":[{"role":"user","content":"legacy body","source_message_id":"evt_live","source_run_id":"run_live"}],"source_message_ledger":{"evt_live":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","evt_pruned":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}' AS BLOB),
                summary = '{"Id":"released-session","Title":"Legacy","CreatedAt":"2026-01-01T00:00:00+00:00","UpdatedAt":"2026-01-02T00:00:00+00:00","MessageCount":1}'
            WHERE session_id='released-session';
            """);

        var result = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.True(result.IsAvailable, result.ErrorCode);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_messages WHERE session_id='released-session' AND message_index=0 AND content='legacy body';"));
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_source_receipts WHERE session_id='released-session';"));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            await fixture.ScalarAsync("SELECT fingerprint FROM business_session_source_receipts WHERE source_message_id='evt_pruned';"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT total_message_count FROM business_sessions WHERE session_id='released-session';"));
        Assert.Equal("[]", await fixture.ScalarAsync("SELECT json_extract(CAST(document AS TEXT),'$.messages') FROM business_sessions WHERE session_id='released-session';"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_message_search WHERE business_session_message_search MATCH 'legacy';"));
    }

    [Fact]
    public async Task V29_to_v30_accepts_a_null_source_ledger_from_the_previous_serializer()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
            "RuAgent.Harness.Persistence.Tests.Fixtures.development-migrations.json")!;
        var fragments = (await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<int, string>>(resource))!;
        await fixture.ExecuteAsync(string.Join('\n', Enumerable.Range(18, 10).Select(version => fragments[version]))
            + "\nPRAGMA user_version=29;");
        await fixture.ExecuteAsync("""
            UPDATE business_sessions SET document=CAST('{"messages":[],"source_message_ledger":null}' AS BLOB)
            WHERE session_id='released-session';
            """);
        var result = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(result.IsAvailable, result.ErrorCode);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_source_receipts;"));
    }

    [Fact]
    public async Task V29_to_v30_rejects_corrupt_valid_json_without_advancing_and_can_retry()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
            "RuAgent.Harness.Persistence.Tests.Fixtures.development-migrations.json")!;
        var fragments = (await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<int, string>>(resource))!;
        var v29 = string.Join('\n', Enumerable.Range(18, 10).Select(version => fragments[version]));
        await fixture.ExecuteAsync(v29 + "\nPRAGMA user_version=29;");
        await fixture.ExecuteAsync("""
            UPDATE business_sessions
            SET document = CAST('{"id":"released-session","title":"Corrupt","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","messages":[{"role":"tool","content":"must not be dropped"}]}' AS BLOB)
            WHERE session_id='released-session';
            """);

        var rejected = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.False(rejected.IsAvailable);
        Assert.Equal("harness_session_history_migration_invalid", rejected.ErrorCode);
        Assert.Equal(29L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='business_session_messages';"));

        await fixture.ExecuteAsync("""
            UPDATE business_sessions
            SET document = CAST('{"id":"released-session","title":"Repaired","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","messages":[{"role":"user","content":"repaired"}]}' AS BLOB)
            WHERE session_id='released-session';
            """);
        var retried = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.True(retried.IsAvailable, retried.ErrorCode);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_messages WHERE content='repaired';"));
    }

    [Theory]
    [InlineData("evt?", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("evt_pruned", "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task V29_to_v30_rejects_invalid_source_receipts_without_dropping_ledger(string sourceId, string fingerprint)
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
            "RuAgent.Harness.Persistence.Tests.Fixtures.development-migrations.json")!;
        var fragments = (await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<int, string>>(resource))!;
        var v29 = string.Join('\n', Enumerable.Range(18, 10).Select(version => fragments[version]));
        await fixture.ExecuteAsync(v29 + "\nPRAGMA user_version=29;");
        var document = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "released-session",
            title = "Legacy",
            created_at = "2026-01-01T00:00:00Z",
            updated_at = "2026-01-02T00:00:00Z",
            messages = Array.Empty<object>(),
            source_message_ledger = new Dictionary<string, string> { [sourceId] = fingerprint },
        }).Replace("'", "''", StringComparison.Ordinal);
        await fixture.ExecuteAsync($"UPDATE business_sessions SET document=CAST('{document}' AS BLOB) WHERE session_id='released-session';");

        var rejected = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.False(rejected.IsAvailable);
        Assert.Equal("harness_session_history_migration_invalid", rejected.ErrorCode);
        Assert.Equal(29L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='business_session_source_receipts';"));
    }

    [Fact]
    public async Task Released_v18_upgrade_preserves_records_and_matches_a_fresh_database()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        Assert.Equal(18L, await fixture.ScalarAsync("PRAGMA user_version"));

        var upgraded = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(upgraded.IsAvailable, upgraded.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, upgraded.SchemaVersion);
        await AssertRecordsPreservedAsync(fixture);

        var freshPath = Path.Combine(Path.GetDirectoryName(fixture.Options.DatabasePath)!, "fresh.db");
        var fresh = await HarnessSqliteSchema.InitializeAsync(new(freshPath), default);
        Assert.True(fresh.IsAvailable, fresh.ErrorCode);
        Assert.Equal(await fixture.SchemaAsync(freshPath), await fixture.SchemaAsync());
        Assert.True((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
        await AssertRecordsPreservedAsync(fixture);
    }

    [Fact]
    public async Task Failure_after_partial_ddl_rolls_back_data_schema_and_version_then_retries()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        await fixture.ExecuteAsync("PRAGMA ignore_check_constraints=ON; UPDATE agent_runs SET state=99; PRAGMA ignore_check_constraints=OFF;");
        var schemaBefore = await fixture.SchemaAsync();

        var attempted = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.False(attempted.IsAvailable);
        Assert.Equal("harness_store_unavailable", attempted.ErrorCode);
        Assert.Equal(18L, await fixture.ScalarAsync("PRAGMA user_version"));
        Assert.Equal(schemaBefore, await fixture.SchemaAsync());
        Assert.Equal("exclusive", await fixture.ScalarAsync("SELECT mode FROM workspace_coordinators"));
        Assert.Equal("DEADBEEF", await fixture.ScalarAsync("SELECT hex(protected_content) FROM agent_payloads"));
        await using (var pooled = await fixture.Options.OpenConnectionAsync(default))
        {
            await using var command = pooled.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        await fixture.ExecuteAsync("UPDATE agent_runs SET state=3;");
        var retried = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(retried.IsAvailable, retried.ErrorCode);
        await AssertRecordsPreservedAsync(fixture);
    }

    [Fact]
    public async Task Two_processes_upgrade_the_released_database_once()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        var gate = fixture.Options.DatabasePath + ".start";
        using var first = StartInitializer(fixture.Options.DatabasePath, gate);
        using var second = StartInitializer(fixture.Options.DatabasePath, gate);
        try
        {
            await File.WriteAllTextAsync(gate, "start");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(first.WaitForExitAsync(deadline.Token), second.WaitForExitAsync(deadline.Token));
            Assert.True(first.ExitCode == 0, await first.StandardOutput.ReadToEndAsync() + await first.StandardError.ReadToEndAsync());
            Assert.True(second.ExitCode == 0, await second.StandardOutput.ReadToEndAsync() + await second.StandardError.ReadToEndAsync());
            Assert.Equal((long)HarnessSqliteSchema.CurrentVersion, await fixture.ScalarAsync("PRAGMA user_version"));
            // The parallel-policy migration increments 7 -> 8 exactly once.
            Assert.Equal(8L, await fixture.ScalarAsync("SELECT version FROM workspace_coordinators"));
            await AssertRecordsPreservedAsync(fixture);
        }
        finally
        {
            foreach (var child in new[] { first, second })
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
    }

    private static Process StartInitializer(string databasePath, string gate)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ReleasedSchemaMigrationTests).Assembly.Location);
        start.ArgumentList.Add("--schema-initialize");
        start.ArgumentList.Add(databasePath);
        start.ArgumentList.Add(gate);
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned schema probe.");
    }

    private static async Task AssertRecordsPreservedAsync(ReleasedSchemaFixture fixture)
    {
        Assert.Equal("standard:2:7", await fixture.ScalarAsync("SELECT preset || ':' || permission_profile_version || ':' || version FROM agent_session_settings"));
        Assert.Equal(4L, await fixture.ScalarAsync("SELECT version FROM agent_runs"));
        Assert.Equal("DEADBEEF", await fixture.ScalarAsync("SELECT hex(protected_content) FROM agent_payloads"));
        Assert.Equal("recorded", await fixture.ScalarAsync("SELECT safe_code FROM agent_events"));
        Assert.Equal("010203", await fixture.ScalarAsync("SELECT hex(document) FROM business_sessions"));
        Assert.Equal("explicit:1", await fixture.ScalarAsync("SELECT trust_source || ':' || trust_decision FROM agent_workspaces"));
        Assert.Equal("shared:32", await fixture.ScalarAsync("SELECT mode || ':' || max_concurrent_runs FROM workspace_coordinators"));
    }
}
