using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

/// <summary>A thinking row whose excerpt was cut names its stored reasoning, and only that.</summary>
public sealed class AgentActivityReasoningPayloadTests
{
    private const string PayloadId = "payload_0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_thinking_row_round_trips_its_reasoning_payload_id()
    {
        var json = AgentActivityWireCodec.Serialize(new AgentActivityWire("act_think1", AgentActivityKind.Thinking,
            AgentActivityState.Completed, Detail: "excerpt…", ReasoningPayloadId: PayloadId));

        Assert.Contains($"\"reasoning_payload_id\":\"{PayloadId}\"", json, StringComparison.Ordinal);
        Assert.Equal(PayloadId, AgentActivityWireCodec.Parse(json).ReasoningPayloadId);
    }

    [Theory]
    [InlineData(AgentActivityKind.Terminal, PayloadId)]
    [InlineData(AgentActivityKind.Thinking, "payload_XYZ")]
    [InlineData(AgentActivityKind.Thinking, "secret_0123456789abcdef0123456789abcdef")]
    public void Any_other_row_or_id_shape_is_refused(AgentActivityKind kind, string id)
    {
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Serialize(
            new AgentActivityWire("act_bad1", kind, AgentActivityState.Completed, ReasoningPayloadId: id)));
    }
}
