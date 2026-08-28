using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentEventModelTests
{
    private const string DurableEnvelope =
        "{\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"run_12345678901234567890123456789012\"," +
        "\"session_id\":\"sess_1\",\"sequence\":5,\"event_type\":\"run.state_changed\"," +
        "\"occurred_at\":\"2026-01-01\",\"status\":\"running\",\"safe_summary\":\"started\"}";

    [Fact]
    public void FromSseFrame_durable_parses_envelope()
    {
        var frame = new SseFrame(5, "message", DurableEnvelope, IsTransient: false, TransientSequence: null);
        var model = AgentEventModel.FromSseFrame(frame);

        Assert.False(model.IsTransient);
        Assert.Equal(5, model.Sequence);
        Assert.Equal("run.state_changed", model.EventType);
        Assert.Equal("started", model.SafeSummary);
        Assert.Equal("evt_1", model.EventId);
    }

    [Fact]
    public void FromSseFrame_transient_marks_transient()
    {
        var frame = new SseFrame(null, "transient", "{\"type\":\"tool.output.delta\"}", IsTransient: true, TransientSequence: 9);
        var model = AgentEventModel.FromSseFrame(frame);

        Assert.True(model.IsTransient);
        Assert.Equal(9, model.TransientSequence);
        Assert.Equal("transient", model.EventType);
    }

    [Fact]
    public void FromSseFrame_final_snapshot_is_not_parsed_as_a_durable_envelope()
    {
        var frame = new SseFrame(
            DurableSequence: null,
            EventType: "final_snapshot",
            Data: "{\"status\":\"cancelled\"}",
            IsTransient: false,
            TransientSequence: null);

        var model = AgentEventModel.FromSseFrame(frame);

        Assert.Equal("final_snapshot", model.EventType);
        Assert.Equal("cancelled", model.Status);
        Assert.Null(model.Sequence);
    }

    [Fact]
    public void ToString_does_not_leak_raw_transient_data()
    {
        var frame = new SseFrame(null, "transient", "{\"command\":\"secret rm -rf /\"}", IsTransient: true, TransientSequence: 1);
        var model = AgentEventModel.FromSseFrame(frame);

        Assert.DoesNotContain("secret", model.ToString());
        Assert.DoesNotContain("rm -rf", model.ToString());
        Assert.Contains("transient", model.ToString());
    }

    [Fact]
    public void ToString_omits_safe_summary()
    {
        var frame = new SseFrame(5, "message", DurableEnvelope, IsTransient: false, TransientSequence: null);
        var model = AgentEventModel.FromSseFrame(frame);

        Assert.DoesNotContain("started", model.ToString());
    }
}
