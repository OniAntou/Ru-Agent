using System.Text.Json.Nodes;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerEnvelopeTests
{
    [Fact]
    public void Hello_round_trips_without_losing_UTF8_payload()
    {
        var source = new WorkerEnvelope(
            1,
            "conn-1",
            WorkerMessageTypes.Hello,
            null,
            new JsonObject
            {
                ["token"] = "opaque-secret",
                ["text"] = "Em nghe đây.",
                ["capabilities"] = new JsonArray("chat", "voice"),
            });

        var encoded = WorkerEnvelopeCodec.Serialize(source);
        var decoded = WorkerEnvelopeCodec.Parse(encoded);

        Assert.Equal(source.V, decoded.V);
        Assert.Equal(source.Id, decoded.Id);
        Assert.Equal(source.Type, decoded.Type);
        Assert.Null(decoded.Seq);
        Assert.Equal("Em nghe đây.", decoded.Payload["text"]!.GetValue<string>());
        Assert.Equal(["chat", "voice"], decoded.Payload["capabilities"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
    }

    [Theory]
    [InlineData("{\"v\":2,\"id\":\"x\",\"type\":\"hello\",\"payload\":{}}", "unsupported_version")]
    [InlineData("{\"v\":1,\"id\":\"\",\"type\":\"hello\",\"payload\":{}}", "invalid_id")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"unknown\",\"payload\":{}}", "unknown_type")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"hello.ok\",\"seq\":-1,\"payload\":{}}", "invalid_sequence")]
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"hello\",\"payload\":[]}", "invalid_payload")]
    public void Parse_rejects_invalid_envelopes_with_stable_codes(string json, string expectedCode)
    {
        var error = Assert.Throws<WorkerProtocolException>(() => WorkerEnvelopeCodec.Parse(json));

        Assert.Equal(expectedCode, error.Code);
        Assert.DoesNotContain(json, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_request_ids_longer_than_128_characters()
    {
        var json =
            $"{{\"v\":1,\"id\":\"{new string('x', 129)}\",\"type\":\"hello\",\"payload\":{{}}}}";

        var error = Assert.Throws<WorkerProtocolException>(() => WorkerEnvelopeCodec.Parse(json));

        Assert.Equal("invalid_id", error.Code);
    }

    [Theory]
    [InlineData("hello.ok")]
    [InlineData("chat.reset")]
    [InlineData("chat.chunk")]
    [InlineData("chat.done")]
    [InlineData("memory.facts")]
    [InlineData("runtime.state")]
    [InlineData("runtime.response")]
    [InlineData("voice.control")]
    [InlineData("voice.done")]
    [InlineData("worker.health.ok")]
    public void Ordered_response_types_require_a_sequence(string messageType)
    {
        var json =
            $"{{\"v\":1,\"id\":\"req-1\",\"type\":\"{messageType}\",\"payload\":{{}}}}";

        var error = Assert.Throws<WorkerProtocolException>(() => WorkerEnvelopeCodec.Parse(json));

        Assert.Equal("missing_sequence", error.Code);
    }

    [Fact]
    public void Diagnostic_summary_never_contains_token_or_payload_values()
    {
        var envelope = new WorkerEnvelope(
            1,
            "request-containing-private-id",
            WorkerMessageTypes.Hello,
            null,
            new JsonObject { ["token"] = "do-not-leak", ["prompt"] = "private prompt" });

        var summary = WorkerDiagnostics.Summarize(envelope);

        Assert.Contains("type=hello", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("request-containing-private-id", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrelationId_round_trips_in_envelope()
    {
        var envelope = new WorkerEnvelope(
            1,
            "req-corr",
            WorkerMessageTypes.Hello,
            null,
            new JsonObject(),
            "c_1234567890abcdef");

        var json = WorkerEnvelopeCodec.Serialize(envelope);
        Assert.Contains("\"correlation_id\":\"c_1234567890abcdef\"", json);

        var parsed = WorkerEnvelopeCodec.Parse(json);
        Assert.Equal("c_1234567890abcdef", parsed.CorrelationId);
    }

    [Fact]
    public void Tool_request_requires_a_bounded_capability_class()
    {
        var envelope = new WorkerEnvelope(
            1,
            "chat-1",
            WorkerMessageTypes.ChatToolRequest,
            0,
            new JsonObject
            {
                ["request_id"] = "tool-1",
                ["session_id"] = "session-1",
                ["correlation_id"] = "corr-1",
                ["tool_id"] = "system.volume",
                ["tool_version"] = "1.0.0",
                ["capability_class"] = "native_mutation",
                ["arguments"] = new JsonObject { ["operation"] = "mute" },
                ["deadline_ms"] = 30_000,
            });

        WorkerEnvelopeCodec.Validate(envelope);
        envelope.Payload.Remove("capability_class");

        var error = Assert.Throws<WorkerProtocolException>(() => WorkerEnvelopeCodec.Validate(envelope));
        Assert.Equal("invalid_tool_request", error.Code);
    }

    [Theory]
    [InlineData(-1, "verified", "0123456789abcdef")]
    [InlineData(300001, "verified", "0123456789abcdef")]
    [InlineData(12, "unknown", "0123456789abcdef")]
    [InlineData(12, "verified", "ABCDEF0123456789")]
    public void Tool_result_rejects_invalid_receipt_metadata(
        int durationMs,
        string verificationState,
        string endpointHash)
    {
        var envelope = new WorkerEnvelope(
            1,
            "chat-1",
            WorkerMessageTypes.ChatToolResult,
            null,
            new JsonObject
            {
                ["request_id"] = "tool-1",
                ["status"] = "succeeded",
                ["output"] = new JsonObject(),
                ["error_code"] = null,
                ["safe_summary"] = "completed",
                ["duration_ms"] = durationMs,
                ["verification_state"] = verificationState,
                ["endpoint_role"] = "default_render_multimedia",
                ["endpoint_hash"] = endpointHash,
            });

        var error = Assert.Throws<WorkerProtocolException>(() => WorkerEnvelopeCodec.Validate(envelope));

        Assert.Equal("invalid_tool_result", error.Code);
    }
}
