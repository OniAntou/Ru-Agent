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
    public async Task Append_adds_unix_and_UTC_defaults_without_mutating_the_input()
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
        Assert.Equal("synthetic", actual.Data["content"]!.GetValue<string>());
        Assert.Equal(0, input.Ts);
        Assert.Equal(string.Empty, input.Iso);
    }

    [Fact]
    public async Task Read_skips_malformed_lines_sorts_by_timestamp_and_clamps_limit()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ActivityLogFile)!);
        await File.WriteAllTextAsync(paths.ActivityLogFile, """
            not-json
            {"ts":3,"iso":"three","type":"chat","session_id":null,"data":{"content":"c"}}
            {"ts":1,"iso":"one","type":"chat","session_id":null,"data":{"content":"a"}}
            {"ts":2,"iso":"two","type":"chat","session_id":null,"data":{"content":"b"}}

            """);

        var lastTwo = await store.ReadAsync(2, CancellationToken.None);
        var clampedToOne = await store.ReadAsync(0, CancellationToken.None);

        Assert.Equal(["b", "c"], lastTwo.Select(item => item.Data["content"]!.GetValue<string>()));
        Assert.Single(clampedToOne);
        Assert.Equal("c", clampedToOne[0].Data["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Append_trims_to_the_newest_5000_physical_lines()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ActivityLogFile)!);
        var existing = Enumerable.Range(0, 5000)
            .Select(index => $"{{\"ts\":{index},\"iso\":\"{index}\",\"type\":\"seed\",\"session_id\":null,\"data\":{{}}}}")
            .ToArray();
        await File.WriteAllLinesAsync(paths.ActivityLogFile, existing);

        await store.AppendAsync(
            new ActivityEvent(5000, "5000", "seed", null, []),
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
                new ActivityEvent(index + 1, $"iso-{index}", "chat", null, new JsonObject
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
    public async Task Append_removes_tokens_audio_and_private_VTS_metadata_recursively()
    {
        var (store, _) = CreateStore();
        await store.AppendAsync(
            new ActivityEvent(1, "iso", "vts_action", null, new JsonObject
            {
                ["codes"] = "private-code",
                ["facial"] = "smile",
                ["body"] = "wave",
                ["api_key"] = "secret",
                ["nested"] = new JsonObject
                {
                    ["audio_b64"] = "audio",
                    ["voice_style_id"] = "private-style",
                    ["safe_count"] = 2,
                },
            }),
            CancellationToken.None);

        var actual = Assert.Single(await store.ReadAsync(200, CancellationToken.None));
        var serialized = actual.Data.ToJsonString();
        Assert.DoesNotContain("private-code", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("smile", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("wave", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("audio", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-style", serialized, StringComparison.Ordinal);
        Assert.Equal(2, actual.Data["nested"]!["safe_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Clear_removes_the_file_and_is_idempotent()
    {
        var (store, paths) = CreateStore();
        await store.AppendAsync(new ActivityEvent(1, "iso", "chat", null, []), CancellationToken.None);

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
