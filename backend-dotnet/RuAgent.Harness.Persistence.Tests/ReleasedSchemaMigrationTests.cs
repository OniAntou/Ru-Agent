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
