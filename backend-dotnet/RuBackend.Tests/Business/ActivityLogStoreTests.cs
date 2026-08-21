using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Activity;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class ActivityLogStoreTests
{
    [Fact]
    public async Task Read_missing_file_returns_an_empty_list()
    {
        var (store, _) = CreateStore();

        Assert.Empty(await store.ReadAsync(200, CancellationToken.None));
    }

    [Fact]
    public async Task Append_adds_unix_and_UTC_defaults_and_transforms_chat_to_preview()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 11, 3, 4, 5, TimeSpan.Zero));
        var (store, _) = CreateStore(clock);
        var input = new ActivityEvent(0, string.Empty, "chat", "s1", new JsonObject
        {
            ["role"] = "user",
            ["content"] = "synthetic",
        });

        await store.AppendAsync(input, CancellationToken.None);
        var actual = Assert.Single(await store.ReadAsync(200, CancellationToken.None));

        Assert.Equal(1786417445d, actual.Ts);
        Assert.Equal("2026-08-11T03:04:05.0000000+00:00", actual.Iso);
        Assert.NotNull(actual.EventId);
        Assert.Equal("synthetic", actual.Data["preview"]!.GetValue<string>());
        Assert.False(actual.Data.ContainsKey("content"));
        Assert.Equal(0, input.Ts);
        Assert.Equal(string.Empty, input.Iso);
    }

    [Fact]
    public async Task Append_suppresses_duplicate_event_ids()
    {
        var (store, _) = CreateStore();
        var evt1 = new ActivityEvent("evt_dup_1", 100, "2026-08-20T00:00:00Z", "chat", null, new JsonObject
        {
            ["role"] = "user",
            ["content"] = "hello",
        });
        var evt2 = new ActivityEvent("evt_dup_1", 101, "2026-08-20T00:00:01Z", "chat", null, new JsonObject
        {
            ["role"] = "user",
            ["content"] = "hello again",
        });

        await store.AppendAsync(evt1, CancellationToken.None);
        await store.AppendAsync(evt2, CancellationToken.None);

        var actual = await store.ReadAsync(200, CancellationToken.None);
        Assert.Single(actual);
        Assert.Equal("evt_dup_1", actual[0].EventId);
    }

    [Fact]
    public async Task Read_skips_malformed_lines_sorts_by_timestamp_and_clamps_limit()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ActivityLogFile)!);
        await File.WriteAllTextAsync(paths.ActivityLogFile, """
            not-json
            {"event_id":"e3","ts":3,"iso":"three","type":"chat","session_id":null,"data":{"preview":"c","original_length":1,"redacted":false}}
            {"event_id":"e1","ts":1,"iso":"one","type":"chat","session_id":null,"data":{"preview":"a","original_length":1,"redacted":false}}
            {"event_id":"e2","ts":2,"iso":"two","type":"chat","session_id":null,"data":{"preview":"b","original_length":1,"redacted":false}}

            """);

        var lastTwo = await store.ReadAsync(2, CancellationToken.None);
        var clampedToOne = await store.ReadAsync(0, CancellationToken.None);

        Assert.Equal(["b", "c"], lastTwo.Select(item => item.Data["preview"]!.GetValue<string>()));
        Assert.Single(clampedToOne);
        Assert.Equal("c", clampedToOne[0].Data["preview"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_resanitizes_legacy_raw_activity_before_returning_it()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ActivityLogFile)!);
        await File.WriteAllTextAsync(paths.ActivityLogFile, """
            {"event_id":"legacy-raw","ts":4,"iso":"four","type":"chat","session_id":"s1","data":{"role":"user","content":"secret Bearer token and prompt"}}
            """);

        var actual = Assert.Single(await store.ReadAsync(200, CancellationToken.None));

        Assert.False(actual.Data.ContainsKey("content"));
        Assert.Equal("secret [REDACTED_TOKEN] and prompt", actual.Data["preview"]!.GetValue<string>());
        Assert.Equal(30, actual.Data["original_length"]!.GetValue<int>());
    }

    [Fact]
    public async Task Append_trims_to_the_newest_5000_physical_lines()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ActivityLogFile)!);
        var existing = Enumerable.Range(0, 5000)
            .Select(index => $"{{\"event_id\":\"e_{index}\",\"ts\":{index},\"iso\":\"{index}\",\"type\":\"chat\",\"session_id\":null,\"data\":{{\"preview\":\"p\",\"original_length\":1,\"redacted\":false}}}}")
            .ToArray();
        await File.WriteAllLinesAsync(paths.ActivityLogFile, existing);

        await store.AppendAsync(
            new ActivityEvent("e_5000", 5000, "5000", "chat", null, new JsonObject { ["content"] = "msg" }),
            CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(paths.ActivityLogFile);
        Assert.Equal(5000, lines.Length);
        Assert.DoesNotContain("\"ts\":0", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"ts\":5000", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_appends_are_serialized_without_lost_or_malformed_lines()
    {
        var (store, paths) = CreateStore();
        var appends = Enumerable.Range(0, 80)
            .Select(index => store.AppendAsync(
                new ActivityEvent($"evt_conc_{index}", index + 1, $"iso-{index}", "chat", null, new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = $"message-{index}",
                }),
                CancellationToken.None));

        await Task.WhenAll(appends);

        Assert.Equal(80, (await store.ReadAsync(500, CancellationToken.None)).Count);
        Assert.Equal(80, (await File.ReadAllLinesAsync(paths.ActivityLogFile)).Length);
    }

    [Fact]
    public async Task Clear_removes_the_file_and_is_idempotent()
    {
        var (store, paths) = CreateStore();
        await store.AppendAsync(new ActivityEvent(1, "iso", "chat", null, new JsonObject { ["content"] = "hi" }), CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);

        Assert.False(File.Exists(paths.ActivityLogFile));
        Assert.Empty(await store.ReadAsync(200, CancellationToken.None));
    }

    private static (ActivityLogStore Store, DataPaths Paths) CreateStore(TimeProvider? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-activity-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "memory_texts.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new ActivityLogStore(paths, clock), paths);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
