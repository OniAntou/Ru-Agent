using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class AgentEnvelopeTests
{
    [Fact]
    public void Serialize_round_trips_identity_and_payload()
    {
        var envelope = new AgentEnvelope(
            1,
            "tool-1",
            AgentMessageTypes.ToolRequest,
            "session-1",
            "corr-1",
            10_000,
            new JsonObject { ["tool_id"] = "files.list" });

        var json = AgentEnvelopeCodec.Serialize(envelope);
        var parsed = AgentEnvelopeCodec.Parse(json);

        Assert.Equal(envelope.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(envelope.RequestId, parsed.RequestId);
        Assert.Equal(envelope.MessageType, parsed.MessageType);
        Assert.Equal(envelope.SessionId, parsed.SessionId);
        Assert.Equal(envelope.CorrelationId, parsed.CorrelationId);
        Assert.Equal(envelope.DeadlineMs, parsed.DeadlineMs);
        Assert.Equal(envelope.Payload.ToJsonString(), parsed.Payload.ToJsonString());
        Assert.Contains("\"protocol_version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"request_id\":\"tool-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_omits_null_correlation_id()
    {
        var envelope = new AgentEnvelope(
            1,
            "health-1",
            AgentMessageTypes.Health,
            "session-1",
            null,
            1_000,
            new JsonObject());

        var json = AgentEnvelopeCodec.Serialize(envelope);

        Assert.DoesNotContain("correlation_id", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_deep_clones_payload()
    {
        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(
            new AgentEnvelope(
                1,
                "request-1",
                AgentMessageTypes.ToolResult,
                "session-1",
                null,
                1_000,
                new JsonObject { ["status"] = "succeeded" })));

        parsed.Payload["status"] = "changed";

        Assert.Equal("changed", parsed.Payload["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("unsupported_version")]
    [InlineData("invalid_id")]
    [InlineData("unknown_type")]
    [InlineData("invalid_payload")]
    public void Parse_rejects_invalid_envelopes(string expectedCode)
    {
        var json = InvalidEnvelopeFor(expectedCode);

        var error = Assert.Throws<AgentProtocolException>(() => AgentEnvelopeCodec.Parse(json));

        Assert.Equal(expectedCode, error.Code);
    }

    private static string InvalidEnvelopeFor(string code)
    {
        var envelope = new JsonObject
        {
            ["protocol_version"] = 1,
            ["message_type"] = AgentMessageTypes.Health,
            ["request_id"] = "request-1",
            ["session_id"] = "session-1",
            ["deadline_ms"] = 1_000,
            ["payload"] = new JsonObject(),
        };

        switch (code)
        {
            case "unsupported_version":
                envelope["protocol_version"] = 99;
                break;
            case "invalid_id":
                envelope["request_id"] = " ";
                break;
            case "unknown_type":
                envelope["message_type"] = "unknown";
                break;
            case "invalid_payload":
                envelope["payload"] = "not-an-object";
                break;
        }

        return envelope.ToJsonString();
    }
}
