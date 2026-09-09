using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests.Harness;

public sealed class HarnessSafeEventEnvelopeTests
{
    [Fact]
    public void Envelope_RoundTrips_UsageFields()
    {
        var envelope = new HarnessSafeEventEnvelope(
            ProtocolVersion: HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: "evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RunId: "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SessionId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sequence: 9,
            EventType: "model.step_completed",
            OccurredAt: "2026-09-09T00:00:00Z",
            Status: "running",
            SafeSummary: "step done",
            InputTokens: 1200,
            OutputTokens: 300,
            TotalTokens: 1500);

        var json = JsonSerializer.Serialize(envelope);
        var back = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(json)!;

        Assert.Equal(1200, back.InputTokens);
        Assert.Equal(300, back.OutputTokens);
        Assert.Equal(1500, back.TotalTokens);
        Assert.Equal(1, HarnessSafeEventEnvelope.CurrentProtocolVersion);
    }

    [Fact]
    public void Envelope_WithoutUsage_ParsesOldJson()
    {
        var oldJson = """{"protocol_version":1,"event_id":"evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run_id":"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","session_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sequence":9,"event_type":"model.step_completed","occurred_at":"2026-09-09T00:00:00Z","status":"running","safe_summary":"step done"}""";
        var back = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(oldJson)!;

        Assert.Null(back.InputTokens);
        Assert.Null(back.OutputTokens);
        Assert.Null(back.TotalTokens);
    }
}
