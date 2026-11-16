using System.Diagnostics;
using Microsoft.Data.Sqlite;
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

    [Theory]
    [InlineData(32, 0)] // the old ceiling and default becomes no cap
    [InlineData(4, 4)] // a smaller cap the user chose is kept
    public async Task V36_to_v37_lifts_the_workspace_run_ceiling(int stored, int expected)
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        var initialized = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(initialized.IsAvailable, initialized.ErrorCode);
        // Recreate the v36 table, whose CHECK still capped the value at 32.
        await fixture.ExecuteAsync($"""
            PRAGMA foreign_keys=OFF;
            CREATE TABLE workspace_coordinators_v36 (
                workspace_id TEXT PRIMARY KEY REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
                mode TEXT NOT NULL CHECK(mode IN ('exclusive', 'shared')),
                max_concurrent_runs INTEGER NOT NULL CHECK(max_concurrent_runs BETWEEN 1 AND 32),
                lease_ttl_seconds INTEGER NOT NULL CHECK(lease_ttl_seconds BETWEEN 5 AND 600),
                merge_text_conflicts INTEGER NOT NULL CHECK(merge_text_conflicts IN (0, 1)),
                external_writer_detection INTEGER NOT NULL CHECK(external_writer_detection IN (0, 1)),
                commit_sequence INTEGER NOT NULL CHECK(commit_sequence >= 0),
                policy_json TEXT NOT NULL CHECK(length(policy_json) BETWEEN 2 AND 16384),
                version INTEGER NOT NULL CHECK(version > 0),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            INSERT INTO workspace_coordinators_v36 SELECT workspace_id, mode, {stored}, lease_ttl_seconds,
                merge_text_conflicts, external_writer_detection, commit_sequence,
                json_set(policy_json, '$.max_concurrent_runs', {stored}), 5, created_at_utc, updated_at_utc
                FROM workspace_coordinators;
            DROP TABLE workspace_coordinators;
            ALTER TABLE workspace_coordinators_v36 RENAME TO workspace_coordinators;
            PRAGMA user_version=36;
            PRAGMA foreign_keys=ON;
            """);
        await using (var pooled = await fixture.Options.OpenConnectionAsync(default))
            SqliteConnection.ClearPool(pooled);

        var upgraded = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.True(upgraded.IsAvailable, upgraded.ErrorCode);
        Assert.Equal($"{expected}:{expected}", await fixture.ScalarAsync(
            "SELECT max_concurrent_runs || ':' || json_extract(policy_json, '$.max_concurrent_runs') FROM workspace_coordinators"));
        // Only a row that changed gets a new policy version.
        Assert.Equal(expected == stored ? 5L : 6L, await fixture.ScalarAsync("SELECT version FROM workspace_coordinators"));
        // The rebuild leaves every row that references the coordinator intact.
        Assert.Null(await fixture.ScalarAsync("PRAGMA foreign_key_check;"));
        Assert.Equal((long)HarnessSqliteSchema.CurrentVersion, await fixture.ScalarAsync("PRAGMA user_version;"));
        // A cap past the old ceiling is now accepted by the table itself.
        await fixture.ExecuteAsync("UPDATE workspace_coordinators SET max_concurrent_runs=500;");
    }

    [Fact]
    public async Task V33_to_v34_backfills_image_facts_for_existing_nested_fork_messages()
    {
        await using var fixture = await ReleasedSchemaFixture.CreateAsync();
        var initialized = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(initialized.IsAvailable, initialized.ErrorCode);
        await fixture.ExecuteAsync("""
            INSERT INTO agent_payloads(payload_id,run_id,kind,audience,encoding,byte_cap,protected_content,
                protection_version,nonce,authentication_tag,created_at_utc)
            VALUES ('payload_55555555555555555555555555555555','run_22222222222222222222222222222222',11,3,0,4096,
                X'01',1,zeroblob(12),zeroblob(16),'2026-10-01');
            INSERT INTO business_sessions(session_id,document,summary,updated_at_utc,parent_session_id)
            VALUES ('fork-session',X'01','{}','2026-10-01','released-session');
            INSERT INTO business_sessions(session_id,document,summary,updated_at_utc,parent_session_id)
            VALUES ('nested-fork-session',X'01','{}','2026-10-01','fork-session');
            INSERT INTO business_session_messages(session_id,message_index,role,content,source_message_id,source_run_id,image_attachment_count)
            VALUES ('released-session',0,'user','parent','evt_parent','run_22222222222222222222222222222222',0),
                ('fork-session',0,'user','fork','evt_fork','run_22222222222222222222222222222222',0),
                ('nested-fork-session',0,'user','nested','evt_nested','run_22222222222222222222222222222222',0);
            DROP INDEX ix_business_session_source_receipts_run_role;
            ALTER TABLE business_session_source_receipts DROP COLUMN source_run_id;
            ALTER TABLE business_session_source_receipts DROP COLUMN source_role;
            ALTER TABLE business_session_messages DROP COLUMN image_attachment_count;
            PRAGMA user_version=33;
            """);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM pragma_table_info('business_session_source_receipts') WHERE name='source_role';"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM pragma_table_info('business_session_messages') WHERE name='image_attachment_count';"));
        Assert.Equal(33L, await fixture.ScalarAsync("PRAGMA user_version;"));
        await using (var pooled = await fixture.Options.OpenConnectionAsync(default))
            SqliteConnection.ClearPool(pooled);

        var upgraded = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.True(upgraded.IsAvailable, upgraded.ErrorCode);
        Assert.Equal(3L, await fixture.ScalarAsync("SELECT count(*) FROM business_session_messages WHERE image_attachment_count=1;"));
        Assert.Equal((long)HarnessSqliteSchema.CurrentVersion, await fixture.ScalarAsync("PRAGMA user_version;"));
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
            // The parallel-policy migration (v19, 7 -> 8) and the no-cap migration (v37,
            // 8 -> 9) each increment the policy version exactly once.
            Assert.Equal(9L, await fixture.ScalarAsync("SELECT version FROM workspace_coordinators"));
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
        // v19 lifted the old exclusive single slot to 32; v37 turns that ceiling into no cap (0).
        Assert.Equal("shared:0", await fixture.ScalarAsync("SELECT mode || ':' || max_concurrent_runs FROM workspace_coordinators"));
    }
}
