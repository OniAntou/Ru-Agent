using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class BusinessSqliteMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-sqlite-" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(Path.Combine(_root, "settings.json"), Path.Combine(_root, "sessions"), Path.Combine(_root, "activity_log.jsonl"));

    [Fact]
    public async Task All_business_stores_write_only_the_existing_database()
    {
        var settings = new SettingsStore(Paths);
        await settings.SaveAsync((await settings.ReadAsync(default)) with { Model = "sqlite-model" }, default);
        var sessions = new SessionStore(Paths);
        var id = (await sessions.CreateAsync(default))!;
        Assert.True(await sessions.AppendAsync(id, [new("user", "persisted")], default));
        await new ActivityLogStore(Paths).AppendAsync(new(1, "1970-01-01T00:00:01Z", "test", id, []), default);

        Assert.True(File.Exists(Paths.AgentHarnessDatabase));
        Assert.False(File.Exists(Paths.SettingsFile));
        Assert.False(Directory.Exists(Paths.SessionDirectory));
        Assert.False(File.Exists(Paths.ActivityLogFile));
        Assert.Equal("sqlite-model", (await new SettingsStore(Paths).ReadAsync(default)).Model);
        Assert.Equal("persisted", Assert.Single((await new SessionStore(Paths).GetAsync(id, default))!.Messages).Content);
        Assert.Single(await new ActivityLogStore(Paths).ReadAsync(10, default));
    }

    [Fact]
    public async Task Legacy_import_does_not_resurrect_deleted_sessions_or_cleared_activity()
    {
        Directory.CreateDirectory(Paths.SessionDirectory);
        const string id = "legacy";
        var document = new SessionDocument(id, "old", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [new("user", "old")]);
        await File.WriteAllTextAsync(Path.Combine(Paths.SessionDirectory, id + ".json"), JsonSerializer.Serialize(document));
        await File.WriteAllTextAsync(Paths.ActivityLogFile, "{\"ts\":1,\"iso\":\"1970-01-01T00:00:01Z\",\"type\":\"test\",\"data\":{}}\n");

        Assert.NotNull(await new SessionStore(Paths).GetAsync(id, default));
        Assert.True(await new SessionStore(Paths).DeleteAsync(id, default));
        await new ActivityLogStore(Paths).ClearAsync(default);

        Assert.Null(await new SessionStore(Paths).GetAsync(id, default));
        Assert.Empty(await new ActivityLogStore(Paths).ReadAsync(10, default));
        Assert.True(File.Exists(Paths.AgentHarnessDatabase));
        Assert.True(File.Exists(Path.Combine(Paths.SessionDirectory, id + ".json")));
    }

    [Fact]
    public async Task Activity_redelivery_after_restart_is_deduplicated_without_read_warmup()
    {
        var item = new ActivityEvent("durable-id", 1, "1970-01-01T00:00:01Z", "test", null, []);
        await new ActivityLogStore(Paths).AppendAsync(item, default);
        await new ActivityLogStore(Paths).AppendAsync(item, default);
        Assert.Single(await new ActivityLogStore(Paths).ReadAsync(10, default));
    }

    [Fact]
    public async Task Settings_import_strips_credentials_and_never_reimports_a_stale_backup()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Paths.SettingsFile, """{"backend":"openai","model":"legacy","api_key":"test-secret","custom_api_key":"other-test-secret","future_setting":42}""");
        var store = new SettingsStore(Paths);
        var snapshot = await store.ReadAsync(default);
        Assert.Equal("legacy", snapshot.Model);
        var persisted = await BusinessSqliteTestData.ReadSettingsAsync(Paths);
        Assert.DoesNotContain("test-secret", persisted);
        Assert.Contains("future_setting", persisted);
        await store.SaveAsync(snapshot with { Model = "current" }, default);
        await File.WriteAllTextAsync(Paths.SettingsFile, """{"backend":"openai","model":"stale"}""");
        Assert.Equal("current", (await new SettingsStore(Paths).ReadAsync(default)).Model);
    }

    [Fact]
    public async Task Concurrent_stores_append_without_losing_messages()
    {
        var id = (await new SessionStore(Paths).CreateAsync(default))!;
        var first = new SessionStore(Paths);
        var second = new SessionStore(Paths);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            (index % 2 == 0 ? first : second).AppendAsync(id, [new("user", $"message-{index}")], default)));
        Assert.All(outcomes, Assert.True);
        var messages = (await new SessionStore(Paths).GetAsync(id, default))!.Messages;
        Assert.Equal(40, messages.Count);
        Assert.Equal(40, messages.Select(message => message.Content).Distinct().Count());
    }

    [Fact]
    public async Task Failed_legacy_import_rolls_back_and_retries_after_the_file_is_released()
    {
        Directory.CreateDirectory(Paths.SessionDirectory);
        foreach (var id in new[] { "first", "locked" })
        {
            await File.WriteAllTextAsync(Path.Combine(Paths.SessionDirectory, id + ".json"),
                JsonSerializer.Serialize(new SessionDocument(id, id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [])));
        }
        var store = new SessionStore(Paths);
        await using (var locked = new FileStream(Path.Combine(Paths.SessionDirectory, "locked.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.ListAsync(default));
            await using var connection = new SqliteConnection($"Data Source={Paths.AgentHarnessDatabase};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM business_sessions) + (SELECT count(*) FROM business_migrations WHERE name = 'sessions');";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        Assert.Equal(2, (await store.ListAsync(default)).Count);
        Assert.Equal(2, (await new SessionStore(Paths).ListAsync(default)).Count);
    }

    [Fact]
    public async Task Source_ledger_survives_pruning_rewind_and_restart()
    {
        var store = new SessionStore(Paths);
        var id = (await store.CreateAsync(default))!;
        var item = new SessionMessage("ai", "durable reply", "source-id", "run-id");
        Assert.Equal(SessionAppendOutcome.Appended, await store.AppendIfAbsentAsync(id, item, default));
        Assert.True(await store.AppendAsync(id, Enumerable.Range(0, 200).Select(i => new SessionMessage("user", $"message-{i}")).ToArray(), default));
        Assert.NotNull(await store.RewindAsync(id, -1, default));
        var restarted = new SessionStore(Paths);
        Assert.Equal(SessionAppendOutcome.AlreadyPresent, await restarted.AppendIfAbsentAsync(id, item, default));
        Assert.Empty((await restarted.GetAsync(id, default))!.Messages);
    }

    [Fact]
    public async Task Legacy_import_respects_existing_harness_deletion_tombstones()
    {
        await new SettingsStore(Paths).ReadAsync(default);
        await BusinessSqliteTestData.ExecuteAsync(Paths, "INSERT INTO agent_session_tombstones(session_id, deleted_at_utc) VALUES ('deleted', '2026-10-09T00:00:00Z');");
        Directory.CreateDirectory(Paths.SessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(Paths.SessionDirectory, "deleted.json"),
            JsonSerializer.Serialize(new SessionDocument("deleted", "deleted", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [])));
        Assert.Empty(await new SessionStore(Paths).ListAsync(default));
        Assert.Null(await new SessionStore(Paths).GetAsync("deleted", default));
    }

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(Paths.AgentHarnessDatabase);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
