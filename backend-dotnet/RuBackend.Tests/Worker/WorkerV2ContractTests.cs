using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerV2ContractTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "worker-v2-protocol.json");

    private static JsonObject LoadFixture()
    {
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "fixtures", "worker-v2-protocol.json"));
        var path = File.Exists(candidate) ? candidate : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "tests", "fixtures", "worker-v2-protocol.json"));
        // Fallback to repo root
        if (!File.Exists(path))
        {
            path = @"D:\LLM\tests\fixtures\worker-v2-protocol.json";
        }

        var json = File.ReadAllText(path);
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("fixture not found");
    }

    [Fact]
    public void Golden_valid_envelopes_round_trip()
    {
        var fixture = LoadFixture();
        var valid = fixture["valid_envelopes"] as JsonArray ?? throw new InvalidOperationException("valid_envelopes missing");
        Assert.Equal(7, valid.Count);
        foreach (var entry in valid)
        {
            var obj = entry as JsonObject ?? throw new InvalidOperationException("entry not object");
            var name = obj["name"]!.GetValue<string>();
            var envelope = obj["envelope"] as JsonObject ?? throw new InvalidOperationException("envelope missing");
            var json = envelope.ToJsonString();
            // Parse via WorkerV2EnvelopeCodec should succeed
            var parsed = WorkerV2EnvelopeCodec.Parse(json);
            var serialized = WorkerV2EnvelopeCodec.Serialize(parsed);
            var reparsed = WorkerV2EnvelopeCodec.Parse(serialized);
            Assert.Equal(parsed.Type, reparsed.Type);
            Assert.Equal(parsed.Id, reparsed.Id);
            // Also validate via contracts
            WorkerV2Contracts.ValidateEnvelope(envelope);
        }
    }

    [Theory]
    [InlineData("wrong_version", "unsupported_version")]
    [InlineData("duplicate_sequence_field", "duplicate_field")]
    [InlineData("v1_tool_result", "unsupported_message")]
    [InlineData("terminal_authority_field", "invalid_envelope")]
    public void Golden_invalid_raw_envelopes_reject_with_expected_code(string name, string expectedCode)
    {
        var fixture = LoadFixture();
        var invalid = fixture["invalid_raw_envelopes"] as JsonArray ?? throw new InvalidOperationException("invalid_raw missing");
        var entry = invalid.FirstOrDefault(e => (e as JsonObject)?["name"]?.GetValue<string>() == name) as JsonObject ?? throw new InvalidOperationException($"{name} not found");
        var raw = entry["json"]!.GetValue<string>();
        var code = entry["code"]!.GetValue<string>();
        Assert.Equal(expectedCode, code);
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2EnvelopeCodec.Parse(raw));
        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public void Rejects_wrong_version()
    {
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["id"] = "step-1",
            ["type"] = "agent.step.cancel",
            ["payload"] = new JsonObject(),
        };
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateEnvelope(envelope));
        Assert.Equal("unsupported_version", ex.Code);
    }

    [Fact]
    public void Rejects_duplicate_sequence()
    {
        var raw = "{\"v\":2,\"id\":\"step-1\",\"type\":\"error\",\"seq\":1,\"seq\":2,\"payload\":{\"code\":\"provider_timeout\",\"retryable\":true}}";
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2EnvelopeCodec.Parse(raw));
        Assert.Equal("duplicate_field", ex.Code);
    }

    [Fact]
    public void Rejects_oversized_schema()
    {
        var largeSchema = new string('a', 32 * 1024 + 1);
        var tool = new JsonObject
        {
            ["tool_id"] = "files.read",
            ["tool_version"] = "1.0.0",
            ["scopes"] = new JsonArray("workspace.read"),
            ["availability"] = "executable",
            ["model_enabled"] = true,
            ["capability_class"] = "tool",
            ["verification_required"] = false,
            ["input_schema"] = new JsonObject { ["type"] = "object", ["title"] = largeSchema },
        };
        var payload = new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["settings"] = new JsonObject { ["model_tools"] = new JsonArray(tool) },
        };
        var envelope = new JsonObject
        {
            ["v"] = 2,
            ["id"] = "step-1",
            ["type"] = "agent.step.start",
            ["payload"] = payload,
        };
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateEnvelope(envelope));
        Assert.Equal("invalid_request", ex.Code);
    }

    [Fact]
    public void Rejects_oversized_arguments()
    {
        var largeArgs = new JsonObject { ["path"] = new string('a', 64 * 1024 + 1) };
        var payload = new JsonObject
        {
            ["text"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["provider_call_id"] = null,
                ["tool_id"] = "files.read",
                ["tool_version"] = "1.0.0",
                ["arguments"] = largeArgs,
            }),
            ["finish_reason"] = "tool_calls",
        };
        var envelope = new JsonObject
        {
            ["v"] = 2,
            ["id"] = "step-1",
            ["type"] = "agent.step.done",
            ["seq"] = 1,
            ["payload"] = payload,
        };
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateEnvelope(envelope));
        Assert.Equal("invalid_step", ex.Code);
    }

    [Fact]
    public void Rejects_tool_result_messages()
    {
        var envelope = new JsonObject
        {
            ["v"] = 2,
            ["id"] = "step-1",
            ["type"] = "chat.tool.result",
            ["payload"] = new JsonObject(),
        };
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateEnvelope(envelope));
        Assert.Equal("unsupported_message", ex.Code);
    }

    [Fact]
    public void Rejects_terminal_fields()
    {
        var envelope = new JsonObject
        {
            ["v"] = 2,
            ["id"] = "step-1",
            ["type"] = "agent.step.cancel",
            ["terminal"] = new JsonObject { ["command"] = "git status" },
            ["payload"] = new JsonObject(),
        };
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateEnvelope(envelope));
        Assert.Equal("invalid_envelope", ex.Code);
    }

    [Fact]
    public void Message_too_large_is_rejected()
    {
        // Construct a raw JSON string >5MB and ensure Parse detects message_too_large before payload validation
        var largeText = new string('a', WorkerV2Contracts.MaxFrameBytes + 1);
        var rawJson = $"{{\"v\":2,\"id\":\"step-1\",\"type\":\"agent.step.delta\",\"seq\":1,\"payload\":{{\"text\":\"{largeText}\"}}}}";
        var ex = Assert.Throws<AgentProtocolException>(() => WorkerV2EnvelopeCodec.Parse(rawJson));
        Assert.Equal("message_too_large", ex.Code);
    }
}
