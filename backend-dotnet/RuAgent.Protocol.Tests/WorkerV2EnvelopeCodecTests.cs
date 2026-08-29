using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class WorkerV2EnvelopeCodecTests
{
    [Fact]
    public void Serialize_and_parse_preserve_the_authenticated_envelope()
    {
        var envelope = new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            "request-1",
            WorkerV2Contracts.AgentStepCancel,
            null,
            new JsonObject());

        var parsed = WorkerV2EnvelopeCodec.Parse(WorkerV2EnvelopeCodec.Serialize(envelope));

        Assert.Equal(envelope.V, parsed.V);
        Assert.Equal(envelope.Id, parsed.Id);
        Assert.Equal(envelope.Type, parsed.Type);
        Assert.Equal(envelope.Payload.ToJsonString(), parsed.Payload.ToJsonString());
    }

    [Fact]
    public void Parse_rejects_duplicate_top_level_fields()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2EnvelopeCodec.Parse(
                "{\"v\":2,\"v\":2,\"id\":\"request-1\",\"type\":\"agent.step.cancel\",\"payload\":{}}"));

        Assert.Equal("duplicate_field", error.Code);
    }

    [Fact]
    public void Parse_applies_the_existing_payload_contract()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2EnvelopeCodec.Parse(
                "{\"v\":2,\"id\":\"request-1\",\"type\":\"agent.step.start\",\"payload\":{\"prompt\":\"missing context\"}}"));

        Assert.Equal("invalid_request", error.Code);
    }
}
