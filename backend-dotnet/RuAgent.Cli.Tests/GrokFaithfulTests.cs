using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class GrokFaithfulTests
{
    [Fact]
    public void Palette_Emits_256Color_Codes_WhenEnabled()
    {
        AnsiTheme.IsEnabled = true;
        try
        {
            Assert.Contains("\u001b[38;5;15m", AnsiTheme.White("x"), StringComparison.Ordinal);
            Assert.Contains("\u001b[38;5;7m", AnsiTheme.Silver("x"), StringComparison.Ordinal);
            Assert.Contains("\u001b[38;5;111m", AnsiTheme.LightBlue("x"), StringComparison.Ordinal);
            Assert.Contains("\u001b[38;5;10m", AnsiTheme.BrightGreen("x"), StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void Palette_Plain_WhenDisabled()
    {
        AnsiTheme.IsEnabled = false;
        Assert.Equal("x", AnsiTheme.White("x"));
        Assert.Equal("x", AnsiTheme.LightBlue("x"));
    }

    [Theory]
    [InlineData(0, "Thinking")]
    [InlineData(4, "Thinking")]
    [InlineData(5, "Reticulating")]
    [InlineData(10, "Computing")]
    [InlineData(15, "Pondering")]
    [InlineData(20, "Percolating")]
    [InlineData(25, "Thinking")]
    public void ThinkingVerb_Rotates_Every_Five_Seconds(int seconds, string expected)
    {
        Assert.Equal(expected, TuiSessionRenderer.PickThinkingVerb(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ToolRequest_Renders_Verb_And_Blue_Path_Without_Diamond()
    {
        AnsiTheme.IsEnabled = false;
        try
        {
            using var sw = new StringWriter();
            TuiEventRenderer.RenderEnvelope(GrokEnvelope("tool.execution_requested", "read op=read path: src/app.cs", 10), sw);
            var output = sw.ToString();
            Assert.Contains("read src/app.cs", output, StringComparison.Ordinal);
            Assert.DoesNotContain("◆", output, StringComparison.Ordinal);
            Assert.DoesNotContain("📖", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void ToolRequest_Uses_Gray_Verb_And_Blue_Path_WhenEnabled()
    {
        AnsiTheme.IsEnabled = true;
        try
        {
            using var sw = new StringWriter();
            TuiEventRenderer.RenderEnvelope(GrokEnvelope("tool.execution_requested", "read op=read path: src/app.cs", 10), sw);
            var output = sw.ToString();
            Assert.Contains("\u001b[90m", output, StringComparison.Ordinal);
            Assert.Contains("\u001b[38;5;111m", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void ToolDone_Renders_Green_Diamond_Card_And_Gutter_Diff()
    {
        AnsiTheme.IsEnabled = false;
        try
        {
            using var sw = new StringWriter();
            TuiEventRenderer.RenderEnvelope(GrokEnvelope(
                "tool.executed",
                "edit completed\n--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n-old line\n+new line",
                11,
                "succeeded"), sw);
            var output = sw.ToString();
            Assert.Contains("◆ edit", output, StringComparison.Ordinal);
            Assert.Contains("┃", output, StringComparison.Ordinal);
            Assert.Contains("+new line", output, StringComparison.Ordinal);
            Assert.Contains("-old line", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void Approval_Renders_Gray_Gutter_Radios_And_Hint_Bar()
    {
        AnsiTheme.IsEnabled = false;
        try
        {
            using var sw = new StringWriter();
            TuiEventRenderer.RenderEnvelope(GrokEnvelope("approval.requested", "Run tests", 12), sw);
            var output = sw.ToString();
            Assert.Contains("┃", output, StringComparison.Ordinal);
            Assert.Contains("(●)", output, StringComparison.Ordinal);
            Assert.Contains("(○)", output, StringComparison.Ordinal);
            Assert.Contains("1/2:select", output, StringComparison.Ordinal);
            Assert.DoesNotContain("╔", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void ThinkingFailed_Renders_Diamond_Notice()
    {
        AnsiTheme.IsEnabled = false;
        try
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);
            var inner = JsonSerializer.Serialize(new { activity_id = "act_11111111111111111111111111111111", kind = "thinking", state = "failed", safe_code = "failed" });
            var outer = JsonSerializer.Serialize(new { kind = "agent.activity", data = inner });
            renderer.RenderFrame(new SseFrame(null, "transient", outer, true, 1));
            var output = sw.ToString();
            Assert.Contains("◆", output, StringComparison.Ordinal);
            Assert.DoesNotContain("• Model thinking failed.", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    private static RuAgent.Protocol.Harness.HarnessSafeEventEnvelope GrokEnvelope(
        string eventType,
        string safeSummary,
        long sequence,
        string status = "running") =>
        new(
            ProtocolVersion: RuAgent.Protocol.Harness.HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: "evt_33333333333333333333333333333333",
            RunId: "run_33333333333333333333333333333333",
            SessionId: "33333333333333333333333333333333",
            Sequence: sequence,
            EventType: eventType,
            OccurredAt: "2026-09-09T00:00:00Z",
            Status: status,
            SafeSummary: safeSummary);
}
