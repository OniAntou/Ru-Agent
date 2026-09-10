using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RuAgent.Cli.Tests;

public sealed class GrokRendererTests
{
    private static HarnessSafeEventEnvelope Envelope(
        string eventType,
        string safeSummary,
        long sequence = 7,
        string status = "running") =>
        new(
            ProtocolVersion: HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: "evt_22222222222222222222222222222222",
            RunId: "run_22222222222222222222222222222222",
            SessionId: "22222222222222222222222222222222",
            Sequence: sequence,
            EventType: eventType,
            OccurredAt: "2026-09-09T00:00:00Z",
            Status: status,
            SafeSummary: safeSummary);

    private static string Render(HarnessSafeEventEnvelope envelope)
    {
        using var sw = new StringWriter();
        TuiEventRenderer.RenderEnvelope(envelope, sw);
        return sw.ToString();
    }

    [Fact]
    public void Renderer_Renders_Grok_Turn_And_Turn_End()
    {
        AnsiTheme.IsEnabled = false;

        var turn = Render(Envelope("model.step_started", "x", sequence: 4));
        Assert.Contains("◆", turn, StringComparison.Ordinal);
        Assert.Contains("Turn #4", turn, StringComparison.Ordinal);

        var end = Render(Envelope("run.completed", "done", sequence: 9, status: "completed"));
        Assert.Contains("Turn completed in", end, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_Renders_Compact_Tool_Lines()
    {
        AnsiTheme.IsEnabled = false;

        var output = Render(Envelope("tool.execution_requested", "read op=read path: src/app.cs", sequence: 10));
        Assert.Contains("read", output, StringComparison.Ordinal);
        Assert.DoesNotContain("📖", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Tool Request #10]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_Hides_Token_Mark_When_Usage_Missing()
    {
        AnsiTheme.IsEnabled = false;

        var envelope = Envelope("model.step_completed", "step done", sequence: 5);
        Assert.Null(envelope.TotalTokens);
        var output = Render(envelope);
        Assert.DoesNotContain("⇣", output, StringComparison.Ordinal);
    }
}
