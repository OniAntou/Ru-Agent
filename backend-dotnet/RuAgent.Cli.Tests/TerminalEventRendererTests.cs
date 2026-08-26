using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TerminalEventRendererTests
{
    private static HarnessSafeEventEnvelope Envelope(
        string eventType,
        string safeSummary,
        long sequence = 7,
        string? payloadRef = null) =>
        new(
            ProtocolVersion: HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: "evt_11111111111111111111111111111111",
            RunId: "run_11111111111111111111111111111111",
            SessionId: "11111111111111111111111111111111",
            Sequence: sequence,
            EventType: eventType,
            OccurredAt: "2026-08-26T08:00:00Z",
            Status: "running",
            SafeSummary: safeSummary,
            PayloadRef: payloadRef);

    private static string RenderDurable(HarnessSafeEventEnvelope envelope)
    {
        using var writer = new StringWriter();
        SafeEventRenderer.Render(envelope, writer);
        return writer.ToString();
    }

    private static string RenderFrame(string json)
    {
        using var writer = new StringWriter();
        SafeEventRenderer.RenderFrame(new SseFrame(DurableSequence: 7, EventType: "event", Data: json, IsTransient: false, TransientSequence: null), writer);
        return writer.ToString();
    }

    [Fact]
    public void Dropped_frames_event_renders_an_explicit_notice()
    {
        var output = RenderDurable(Envelope("transient.frames_dropped", "dropped=12"));

        Assert.Contains("dropped", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconciliation_events_render_the_safe_intent_code()
    {
        var required = RenderDurable(Envelope("reconciliation.required", "recovery_ambiguous"));
        var resolved = RenderDurable(Envelope("reconciliation.resolved", "applied"));

        Assert.Contains("recovery_ambiguous", required, StringComparison.Ordinal);
        Assert.Contains("applied", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_delta_transient_frames_render_bounded_and_safe()
    {
        var data = JsonSerializer.Serialize(new { kind = "tool.output.delta", data = new string('x', 500) });
        using var writer = new StringWriter();
        SafeEventRenderer.RenderFrame(new SseFrame(DurableSequence: null, EventType: "tool.output.delta", Data: data, IsTransient: true, TransientSequence: 3), writer);
        var output = writer.ToString();

        Assert.Contains("[transient:3]", output, StringComparison.Ordinal);
        Assert.Contains("tool.output.delta", output, StringComparison.Ordinal);
        Assert.True(output.Length < 400, $"renderer leaked unbounded transient content: {output.Length}");
    }

    [Fact]
    public void Transient_frames_with_forbidden_fields_are_redacted()
    {
        var data = """{"kind":"tool.output.delta","data":{"command":"git push origin main"}}""";
        using var writer = new StringWriter();
        SafeEventRenderer.RenderFrame(new SseFrame(DurableSequence: null, EventType: "tool.output.delta", Data: data, IsTransient: true, TransientSequence: 4), writer);

        Assert.DoesNotContain("git push", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Final_payload_is_referenced_by_id_only_never_content()
    {
        var output = RenderDurable(Envelope(
            "tool.execution_completed",
            "succeeded",
            payloadRef: "payload_11111111111111111111111111111111"));

        Assert.Contains("payload_11111111111111111111111111111111", output, StringComparison.Ordinal);
        // The head/tail bytes themselves are only fetched by an explicit `agent output`
        // command; the event stream must not inline any tool stdout/stderr content here.
        Assert.DoesNotContain("stdout_head", output, StringComparison.Ordinal);
    }
}
