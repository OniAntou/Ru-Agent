using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Activity;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class ActivityLogMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;

    public ActivityLogMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ru-act-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(
            Path.Combine(_tempDir, "memory.db"),
            Path.Combine(_tempDir, "memory_texts.json"),
            Path.Combine(_tempDir, "settings.json"),
            Path.Combine(_tempDir, "history.json"),
            Path.Combine(_tempDir, "sessions"),
            Path.Combine(_tempDir, "activity_log.jsonl"),
            Path.Combine(_tempDir, "logs"),
            Path.Combine(_tempDir, "logs", "bootstrap"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task Migrate_replaces_full_content_with_preview_and_adds_event_id()
    {
        var legacyJsonl = """
        {"ts":100,"iso":"2026-08-20T00:00:00Z","type":"chat","session_id":"s1","data":{"role":"user","content":"Secret message containing sk-1234567890abcdef"}}
        {"ts":101,"iso":"2026-08-20T00:00:01Z","type":"chat","session_id":"s1","data":{"role":"assistant","content":"Response to user"}}
        {"ts":102,"iso":"2026-08-20T00:00:02Z","type":123,"data":{}}
        """;

        await File.WriteAllTextAsync(_paths.ActivityLogFile, legacyJsonl);

        var service = new ActivityLogMigrationService(_paths);
        await service.MigrateAsync();

        var lines = await File.ReadAllLinesAsync(_paths.ActivityLogFile);
        Assert.Equal(2, lines.Length);

        foreach (var line in lines)
        {
            var node = JsonNode.Parse(line)!.AsObject();
            Assert.True(node.ContainsKey("event_id"));
            Assert.StartsWith("evt_", node["event_id"]!.GetValue<string>());
            Assert.False(node["data"]!.AsObject().ContainsKey("content"));
            Assert.True(node["data"]!.AsObject().ContainsKey("preview"));
            Assert.DoesNotContain("sk-1234567890abcdef", line);
        }
    }
}
