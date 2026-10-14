using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Web;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolBoundaryRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-tool-boundary-" + Guid.NewGuid().ToString("N"));
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ToolBoundaryRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("é")]
    [InlineData("界")]
    [InlineData("\U0001F600")]
    public void Read_small_window_returns_a_repairable_error_instead_of_a_zero_progress_page(string character)
    {
        File.WriteAllText(Path.Combine(_root, "text.txt"), character + "x", StrictUtf8);
        var error = Assert.Throws<ArgumentException>(() => new FileReadAdapter(_root).Execute(
            new JsonObject { ["path"] = "text.txt", ["limit"] = 1 }));
        Assert.Equal("read_limit_too_small", error.Message);
        var repaired = new FileReadAdapter(_root).Execute(new JsonObject { ["path"] = "text.txt", ["limit"] = 4 });
        Assert.StartsWith(character, repaired["content"]!.GetValue<string>());
        Assert.True(repaired["bytes_read"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Read_rejects_an_offset_inside_a_utf8_character()
    {
        File.WriteAllText(Path.Combine(_root, "text.txt"), "é", StrictUtf8);
        var error = Assert.Throws<ArgumentException>(() => new FileReadAdapter(_root).Execute(
            new JsonObject { ["path"] = "text.txt", ["offset"] = 1, ["limit"] = 1 }));
        Assert.Equal("invalid_utf8_offset", error.Message);
    }

    [Fact]
    public void Read_does_not_hide_an_incomplete_character_at_eof()
    {
        File.WriteAllBytes(Path.Combine(_root, "text.txt"), [0x61, 0xc3]);
        var error = Assert.Throws<ArgumentException>(() => new FileReadAdapter(_root).Execute(
            new JsonObject { ["path"] = "text.txt" }));
        Assert.Equal("invalid_utf8", error.Message);
    }

    [Theory]
    [InlineData(true, "^class")]
    [InlineData(false, "class")]
    public async Task Grep_ignores_utf8_bom_for_first_line_matches_and_columns(bool regex, string query)
    {
        File.WriteAllText(Path.Combine(_root, "text.cs"), "class Test {}", new UTF8Encoding(true));
        var output = await new FileSearchAdapter(_root).ExecuteAsync(
            new JsonObject { ["query"] = query, ["is_regex"] = regex }, CancellationToken.None);
        var match = Assert.Single(output["results"]!.AsArray())!;
        Assert.Equal(1, match["column"]!.GetValue<int>());
        Assert.Equal("class Test {}", match["preview"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(31999, 32000)]
    public void Web_pages_roundtrip_unicode_and_cover_the_document(int prefixLength, int limit)
    {
        var source = new string('A', prefixLength) + "\U0001F600B";
        var sanitizer = new WebContentSanitizer();
        var rebuilt = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var page = sanitizer.Extract("demo", new Uri("https://example.com/"), source, false, limit, offset);
            _ = StrictUtf8.GetBytes(page.Text);
            var json = new JsonObject { ["content"] = page.Text }.ToJsonString();
            Assert.Equal(page.Text, JsonNode.Parse(json)!["content"]!.GetValue<string>());
            var body = page.Text.Split("\ncontent: ", 2)[1].Split("\n[/untrusted_web_reference]", 2)[0];
            if (page.NextOffset is not null) body = body[..^"...[truncated]".Length];
            rebuilt.Append(body);
            if (page.NextOffset is null) break;
            Assert.Equal(offset + body.Length, page.NextOffset);
            Assert.True(page.NextOffset > offset);
            offset = page.NextOffset.Value;
        }
        Assert.Equal(source, rebuilt.ToString());
    }

    [Fact]
    public void Web_limit_too_small_for_emoji_has_a_named_failure()
    {
        var error = Assert.Throws<InvalidDataException>(() => new WebContentSanitizer().Extract(
            "demo", new Uri("https://example.com/"), "\U0001F600", false, 1));
        Assert.Equal("web_limit_too_small", error.Message);
    }

    [Fact]
    public void Web_rejects_an_offset_inside_an_emoji()
    {
        var error = Assert.Throws<InvalidDataException>(() => new WebContentSanitizer().Extract(
            "demo", new Uri("https://example.com/"), "\U0001F600", false, 2, 1));
        Assert.Equal("web_offset_invalid_boundary", error.Message);
    }

    [Fact]
    public void Glob_request_deadline_does_not_depend_on_a_cancellation_timer()
    {
        for (var index = 0; index < 1024; index++) File.WriteAllText(Path.Combine(_root, $"{index:D4}.cs"), "// fixture");
        var error = Assert.Throws<TimeoutException>(() => new FileGlobAdapter(_root).Execute(
            new JsonObject { ["pattern"] = "*.cs" }, CancellationToken.None, deadlineMs: 1));
        Assert.Equal("process_timed_out", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Glob_observes_deadline_and_caller_cancellation(bool callerCancelled)
    {
        for (var index = 0; index < 1024; index++) File.WriteAllText(Path.Combine(_root, $"{index:D4}.cs"), "// fixture");
        var session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(5));
        session.Open("boundary", new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SystemRead]));
        await using var service = new ToolExecutionService(session, _root);
        using var cancellation = new CancellationTokenSource();
        var cancelTask = callerCancelled
            ? Task.Factory.StartNew(() =>
            {
                Thread.Sleep(1);
                cancellation.Cancel();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : Task.CompletedTask;
        try
        {
            var result = await service.ExecuteAsync(new AgentEnvelope(1, "glob-boundary", AgentMessageTypes.ToolRequest,
                "boundary", null, callerCancelled ? 30000 : 1, new JsonObject
                {
                    ["tool_id"] = "glob", ["tool_version"] = "1.0.0",
                    ["arguments"] = new JsonObject { ["pattern"] = "*.cs" },
                }), cancellation.Token);
            Assert.Equal(callerCancelled ? ToolResultStatuses.Cancelled : ToolResultStatuses.TimedOut, result.Status);
            Assert.Contains(result.ErrorCode, callerCancelled ? new[] { "cancelled", "process_cancelled" } : new[] { "process_timed_out" });
        }
        finally
        {
            await cancelTask;
            await service.RemoveSessionAsync("boundary");
        }
    }
}
