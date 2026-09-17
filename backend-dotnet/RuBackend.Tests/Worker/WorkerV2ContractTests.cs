using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Worker;
using RuBackend.Harness.Worker;
using RuBackend.Worker;
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
    public void Step_done_accepts_usage_and_reasoning_beside_a_native_call()
    {
        static string Digest() => new('a', 64);
        var payload = new JsonObject
        {
            // A tool selection step stays text-free; narration streams as deltas.
            ["text"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["provider_call_id"] = "call_1",
                ["tool_id"] = "write",
                ["tool_version"] = "1.0.0",
                ["arguments"] = new JsonObject { ["path"] = "index.html" },
            }),
            ["finish_reason"] = "tool_calls",
            ["prompt_receipt"] = new JsonObject
            {
                ["composer_version"] = "1.0.0",
                ["prompt_sha256"] = Digest(),
                ["block_digests"] = new JsonArray(new JsonObject
                {
                    ["block_id"] = "agent.contract",
                    ["sha256"] = Digest(),
                }),
            },
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 10,
                ["output_tokens"] = 5,
            },
            ["reasoning_text"] = "Suy nghi ngan.",
        };
        var envelope = new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            "step_done_regression",
            WorkerV2Contracts.AgentStepDone,
            1,
            null,
            payload);

        var json = WorkerV2EnvelopeCodec.Serialize(envelope);

        var reparsed = WorkerV2EnvelopeCodec.Parse(json);
        Assert.Equal(WorkerV2Contracts.AgentStepDone, reparsed.Type);
        Assert.Equal("Suy nghi ngan.", reparsed.Payload["reasoning_text"]!.GetValue<string>());
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
            ["tool_id"] = "read",
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
    public void Rejects_retired_generation_settings()
    {
        var payload = new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["settings"] = new JsonObject { ["temperature"] = 0.4 },
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
    public void Rejects_retired_web_search_setting()
    {
        var payload = new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(PromptContextFixture.Create()),
            ["settings"] = new JsonObject { ["allow_web_search"] = false },
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
    public void Runtime_capabilities_advertise_both_web_tools()
    {
        var runtimeTools = RuBackend.Harness.Prompting.AgentToolRuntimeProjection.Select(
            new RuBackend.Harness.Prompting.AgentToolRuntimeAvailability(true, true));
        var search = Assert.Single(runtimeTools, definition => definition.ToolId == "web_search");
        var fetch = Assert.Single(runtimeTools, definition => definition.ToolId == "web_fetch");
        Assert.Equal("network.search", search.Scope);
        Assert.Equal("network.fetch", fetch.Scope);
        Assert.True(search.InputSchema["properties"]!.AsObject().ContainsKey("max_results"));

        var offlineTools = RuBackend.Harness.Prompting.AgentToolRuntimeProjection.Select(
            new RuBackend.Harness.Prompting.AgentToolRuntimeAvailability(false, false));
        Assert.DoesNotContain(offlineTools, definition => definition.ToolId == "web_search");
        Assert.DoesNotContain(offlineTools, definition => definition.ToolId == "web_fetch");
        var read = Assert.Single(offlineTools, definition => definition.ToolId == "read");
        // read has no op discriminator at all now, so the web tools cannot appear in one.
        Assert.False(read.InputSchema["properties"]!.AsObject().ContainsKey("op"));
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
                ["tool_id"] = "read",
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

    [Fact]
    public void Prompt_wire_writes_typed_context_without_legacy_memory_field()
    {
        var context = PromptContextFixture.Create();

        var payload = WorkerV2PromptCodec.WriteContext(context);

        Assert.Equal(
            ["schema_version", "agent_profile", "preset", "environment", "instruction_blocks", "capabilities", "runtime_context"],
            payload.Select(property => property.Key));
        Assert.False(payload.ContainsKey("memory_context"));
        Assert.Equal(
            ["root:Ru.md"],
            payload["instruction_blocks"]!.AsArray()
                .Select(item => item!["source_id"]!.GetValue<string>()));
        Assert.Equal(
            ["read"],
            payload["capabilities"]!.AsArray()
                .Select(item => item!["id"]!.GetValue<string>()));
        Assert.Equal(
            "untrusted tool result",
            payload["runtime_context"]!.AsObject()["references"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Prompt_wire_receipt_is_strictly_bound_to_context_and_contains_no_prompt_text()
    {
        var context = PromptContextFixture.Create();
        var receipt = PromptContextFixture.Receipt(context);

        var parsed = WorkerV2PromptCodec.ReadReceipt(receipt, context);
        Assert.Equal("1.0.0", parsed.ComposerVersion);
        Assert.DoesNotContain("text", WorkerV2PromptCodec.WriteReceipt(parsed).ToJsonString(), StringComparison.Ordinal);

        var unknown = (JsonObject)receipt.DeepClone();
        unknown["unexpected"] = true;
        AssertInvalidReceipt(unknown, context);

        var duplicate = (JsonObject)receipt.DeepClone();
        duplicate["block_digests"]!.AsArray().Add(
            new JsonObject { ["block_id"] = "ru.persona", ["sha256"] = new string('d', 64) });
        AssertInvalidReceipt(duplicate, context);

        var mismatchedInstruction = (JsonObject)receipt.DeepClone();
        var instruction = mismatchedInstruction["block_digests"]!.AsArray()
            .Single(item => item!["block_id"]!.GetValue<string>() == "instruction.root:Ru.md")!;
        instruction["sha256"] = new string('e', 64);
        AssertInvalidReceipt(mismatchedInstruction, context);

        var uppercase = (JsonObject)receipt.DeepClone();
        uppercase["prompt_sha256"] = new string('A', 64);
        AssertInvalidReceipt(uppercase, context);
        Assert.Throws<AgentProtocolException>(() => WorkerV2PromptCodec.ReadReceipt(null, context));
    }

    private static void AssertInvalidReceipt(JsonObject receipt, SystemPromptContext context)
    {
        // The shared codec is protocol-owned, so it raises the protocol exception;
        // LoopbackModelStepRunner maps it to the host WorkerProtocolException.
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2PromptCodec.ReadReceipt(receipt, context));
        Assert.Equal("invalid_step", error.Code);
    }

    private static class PromptContextFixture
    {
        public static SystemPromptContext Create()
        {
            const string content = "root guidance";
            return new SystemPromptContext(
                1,
                "ru-agent",
                "standard",
                new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-08-29", "LLM"),
                [new PromptInstructionBlock(
                    "root:Ru.md",
                    "Ru.md",
                    content,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant())],
                [
                    Capability("read"),
                ],
                new PromptRuntimeContext(
                    null,
                    null,
                    [new PromptReference("tool_result", "untrusted", "untrusted tool result")]));
        }

        private static PromptCapability Capability(string toolId)
        {
            var definition = AgentToolDefinitionCatalog.Find(toolId, "1.0.0")!;
            var schema = definition.InputSchema.ToJsonString();
            return new PromptCapability(
                definition.ToolId,
                definition.ToolVersion,
                definition.Description,
                definition.Scope,
                schema,
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                definition.ExampleArguments?.ToJsonString(),
                "enabled");
        }

        public static JsonObject Receipt(SystemPromptContext context)
        {
            var zero = new string('0', 64);
            var blocks = new JsonArray(
                new JsonObject { ["block_id"] = "ru.persona", ["sha256"] = zero },
                new JsonObject { ["block_id"] = "agent.contract", ["sha256"] = zero },
                new JsonObject { ["block_id"] = "environment", ["sha256"] = zero },
                new JsonObject
                {
                    ["block_id"] = "instruction.root:Ru.md",
                    ["sha256"] = context.InstructionBlocks[0].ContentSha256,
                },
                new JsonObject { ["block_id"] = "profile", ["sha256"] = zero },
                new JsonObject { ["block_id"] = "capabilities", ["sha256"] = zero },
                new JsonObject { ["block_id"] = "runtime", ["sha256"] = zero });
            return new JsonObject
            {
                ["composer_version"] = "1.0.0",
                ["prompt_sha256"] = zero,
                ["block_digests"] = blocks,
            };
        }
    }
}
