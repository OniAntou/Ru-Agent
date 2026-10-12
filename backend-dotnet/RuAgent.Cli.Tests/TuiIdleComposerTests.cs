using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Transcript rendering of tool rows and fenced code with the full-screen card layout.
/// </summary>
public sealed class TuiIdleComposerTests
{
    [Fact]
    public void TranscriptFullScreenCards_RendersInlineToolRowAndExpandsTheDiff()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var renderer = new TuiSessionRenderer(writer) { Width = 80 };
        renderer.Transcript.FullScreenCards = true;
        renderer.BeginTurn();

        renderer.RenderFrame(Activity("edit", "started", 1, "app.cs", "Edit"));
        renderer.RenderFrame(Durable(
            "tool.execution_completed", "succeeded",
            "@@ -1 +1 @@\n- old line\n+ new line", 2));

        var text = writer.ToString();
        Assert.Contains("✓ Edit  app.cs", TuiText.Strip(text), StringComparison.Ordinal);
        Assert.Contains("ctrl+o", text, StringComparison.Ordinal);
        Assert.DoesNotContain("╭─", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- old line", text, StringComparison.Ordinal);

        renderer.ToggleExpand();
        text = writer.ToString();
        Assert.Contains("- old line", text, StringComparison.Ordinal);
        Assert.Contains("+ new line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptFullScreenCards_HighlightsFencedCode()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var renderer = new TuiSessionRenderer(writer) { Width = 80 };
        renderer.Transcript.FullScreenCards = true;
        renderer.BeginTurn();

        renderer.RenderFrame(Delta("```cs\nvar x = 1;\n```\n", 1));
        renderer.Flush();

        Assert.Contains("  1 │ var x = 1;", writer.ToString(), StringComparison.Ordinal);
    }

    private static SseFrame Activity(
        string kind, string state, long sequence, string? fileName = null, string? toolDisplay = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["activity_id"] = "act_" + new string('b', 32),
            ["kind"] = kind,
            ["state"] = state,
            ["safe_code"] = "succeeded",
        };
        if (fileName is not null) payload["file_name"] = fileName;
        if (toolDisplay is not null) payload["tool_display"] = toolDisplay;
        var outer = JsonSerializer.Serialize(new { kind = "agent.activity", data = JsonSerializer.Serialize(payload) });
        return new SseFrame(null, "transient", outer, true, sequence);
    }

    private static SseFrame Delta(string text, long sequence)
    {
        var inner = JsonSerializer.Serialize(new { text, model_step_id = "step_" + new string('a', 32) });
        var outer = JsonSerializer.Serialize(new { kind = "assistant.delta", data = inner });
        return new SseFrame(null, "transient", outer, true, sequence);
    }

    private static SseFrame Durable(string eventType, string status, string safeSummary, long sequence) =>
        new(
            sequence,
            "message",
            JsonSerializer.Serialize(new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_" + sequence,
                RunId: "run_" + new string('c', 32),
                SessionId: "sess_1",
                Sequence: sequence,
                EventType: eventType,
                OccurredAt: "2026-10-02T00:00:00Z",
                Status: status,
                SafeSummary: safeSummary)),
            false,
            null);
}
