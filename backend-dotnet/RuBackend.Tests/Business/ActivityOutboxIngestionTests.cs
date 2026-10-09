using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Activity;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class ActivityOutboxIngestionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly ActivityLogStore _store;

    public ActivityOutboxIngestionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ru-act-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(Path.Combine(_tempDir, "settings.json"),
            Path.Combine(_tempDir, "sessions"),
            Path.Combine(_tempDir, "activity_log.jsonl"),
            Path.Combine(_tempDir, "logs"),
            Path.Combine(_tempDir, "logs", "bootstrap"));
        Directory.CreateDirectory(_paths.LogRoot);
        _store = new ActivityLogStore(_paths);
    }

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(_paths.AgentHarnessDatabase);
        Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Migration_imports_legacy_outbox_and_deduplicates_into_canonical_store()
    {
        var outboxPath = Path.Combine(_paths.LogRoot, "activity_outbox.jsonl");
        var outboxLines = new[]
        {
            "{\"event_id\":\"evt_w1\",\"ts\":100.0,\"iso\":\"2026-08-20T00:00:00.000Z\",\"type\":\"chat\",\"session_id\":\"s1\",\"data\":{\"role\":\"assistant\",\"preview\":\"worker message 1\"}}",
            "not a valid json line",
            "{\"event_id\":\"evt_w2\",\"ts\":200.0,\"iso\":\"2026-08-20T00:01:00.000Z\",\"type\":\"vts_action\",\"session_id\":null,\"data\":{\"category\":\"expression\",\"status\":\"succeeded\"}}",
            "{\"event_id\":\"evt_w1\",\"ts\":100.0,\"iso\":\"2026-08-20T00:00:00.000Z\",\"type\":\"chat\",\"session_id\":\"s1\",\"data\":{\"role\":\"assistant\",\"preview\":\"worker message 1 dup\"}}"
        };

        await File.WriteAllLinesAsync(outboxPath, outboxLines);


        var events = await _store.ReadAsync(100, CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Equal("evt_w1", events[0].EventId);
        Assert.Equal("worker message 1", events[0].Data["preview"]!.GetValue<string>());
        Assert.Equal("evt_w2", events[1].EventId);
        Assert.Equal("expression", events[1].Data["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task Migration_skips_malformed_scalars_and_preserves_later_events()
    {
        var outboxPath = Path.Combine(_paths.LogRoot, "activity_outbox.jsonl");
        await File.WriteAllLinesAsync(outboxPath, new[]
        {
            "{\"event_id\":\"evt_before\",\"ts\":100.0,\"iso\":\"2026-08-20T00:00:00Z\",\"type\":\"chat\",\"data\":{\"preview\":\"before\"}}",
            "{\"event_id\":\"evt_bad\",\"ts\":101.0,\"iso\":\"2026-08-20T00:00:01Z\",\"type\":123,\"data\":{}}",
            "{\"event_id\":\"evt_after\",\"ts\":102.0,\"iso\":\"2026-08-20T00:00:02Z\",\"type\":\"chat\",\"data\":{\"preview\":\"after\"}}",
        });

        var events = await _store.ReadAsync(100, CancellationToken.None);
        Assert.Equal(new[] { "evt_before", "evt_after" }, events.Select(item => item.EventId));
        Assert.Equal("chat", events[1].Type);
        Assert.True(File.Exists(outboxPath));
        Assert.Equal(2, (await new ActivityLogStore(_paths).ReadAsync(100, default)).Count);
    }
}
