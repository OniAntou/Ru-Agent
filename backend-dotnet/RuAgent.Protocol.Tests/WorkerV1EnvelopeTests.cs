using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class WorkerV1EnvelopeTests
{
    [Fact]
    public void Parse_accepts_valid_request_and_normalizes_correlation_alias()
    {
        var parsed = WorkerV1EnvelopeCodec.Parse(
            "{\"v\":1,\"id\":\"req-1\",\"type\":\"voice.start\",\"payload\":{},\"correlationId\":\"corr-1\"}");

        Assert.Equal(WorkerV1Contracts.ProtocolVersion, parsed.Version);
        Assert.Equal("req-1", parsed.Id);
        Assert.Equal("voice.start", parsed.Type);
        Assert.Equal("corr-1", parsed.CorrelationId);
    }

    [Fact]
    public void Serialize_and_parse_preserve_a_response_envelope()
    {
        var envelope = new WorkerV1Envelope(
            WorkerV1Contracts.ProtocolVersion,
            "req-1",
            WorkerV1Contracts.VoiceDone,
            0,
            "corr-1",
            new JsonObject { ["state"] = "done" });

        var parsed = WorkerV1EnvelopeCodec.Parse(WorkerV1EnvelopeCodec.Serialize(envelope));

        Assert.Equal(envelope.Version, parsed.Version);
        Assert.Equal(envelope.Id, parsed.Id);
        Assert.Equal(envelope.Type, parsed.Type);
        Assert.Equal(envelope.Sequence, parsed.Sequence);
        Assert.Equal(envelope.CorrelationId, parsed.CorrelationId);
        Assert.Equal(envelope.Payload.ToJsonString(), parsed.Payload.ToJsonString());
    }

    [Theory]
    [InlineData("{\"v\":2,\"id\":\"x\",\"type\":\"hello\",\"payload\":{}}", "unsupported_version")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"voice.done\",\"payload\":{}}", "missing_sequence")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"not-known\",\"payload\":{}}", "unknown_type")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"voice.done\",\"seq\":-1,\"payload\":{}}", "invalid_sequence")]
    public void Parse_rejects_invalid_envelopes(string json, string code)
    {
        var error = Assert.Throws<AgentProtocolException>(() => WorkerV1EnvelopeCodec.Parse(json));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Parse_rejects_duplicate_top_level_fields()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkerV1EnvelopeCodec.Parse(
                "{\"v\":1,\"id\":\"x\",\"id\":\"y\",\"type\":\"hello\",\"payload\":{}}"));

        Assert.Equal("duplicate_field", error.Code);
    }

    [Fact]
    public void Parse_rejects_a_frame_over_the_v1_limit()
    {
        var payload = new string('x', WorkerV1Contracts.MaxFrameBytes);
        var json = $"{{\"v\":1,\"id\":\"x\",\"type\":\"hello\",\"payload\":{{\"value\":\"{payload}\"}}}}";

        var error = Assert.Throws<AgentProtocolException>(() => WorkerV1EnvelopeCodec.Parse(json));

        Assert.Equal("message_too_large", error.Code);
    }
}
