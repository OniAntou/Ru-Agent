using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class WorkerV2ContractHardeningTests
{
    [Fact]
    public void Duplicate_fields_are_rejected_at_any_depth()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2EnvelopeCodec.Parse(
                "{\"v\":2,\"id\":\"request-1\",\"type\":\"agent.step.start\",\"payload\":{\"prompt\":\"x\",\"prompt\":\"x\"}}"));

        Assert.Equal("duplicate_field", error.Code);
    }

    [Fact]
    public void Max_tokens_must_be_a_bounded_integer()
    {
        foreach (var value in new JsonNode?[] { 0, -1, "256", 1.5, true, new JsonObject() })
        {
            var payload = StartPayload();
            payload["settings"]!["max_tokens"] = value?.DeepClone();
            var error = Assert.Throws<AgentProtocolException>(() => WorkerV2Contracts.ValidateStart(payload));
            Assert.Equal("invalid_request", error.Code);
        }

        var accepted = StartPayload();
        accepted["settings"]!["max_tokens"] = 256;
        WorkerV2Contracts.ValidateStart(accepted);
    }

    [Fact]
    public void Event_contract_allowlist_covers_every_durable_kind()
    {
        foreach (var kind in new[]
        {
            "reconciliation.resolved",
            "context.compacted",
            "model.usage_recorded",
            "model.reasoning_recorded",
        })
        {
            Assert.True(HarnessEventContractValidator.IsAllowedEventType(kind));
        }

        // Host-internal prompt receipts advance the SSE cursor without mapping.
        Assert.False(HarnessEventContractValidator.IsAllowedEventType("model.prompt_composed"));
    }

    private static JsonObject StartPayload()
    {
        var profile = AgentPolicyProfileCatalog.Get("standard");
        return new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = new JsonObject
            {
                ["schema_version"] = WorkerV2Contracts.PromptContextSchemaVersion,
                ["agent_profile"] = "ru-agent",
                ["preset"] = "standard",
                ["effective_policy"] = AgentPolicyProfileCatalog.WriteProfile(profile),
                ["environment"] = new JsonObject
                {
                    ["model"] = "model",
                    ["platform"] = "windows",
                    ["locale"] = "vi-VN",
                    ["timezone"] = "Asia/Bangkok",
                    ["date"] = "2026-09-29",
                    ["workspace_label"] = null,
                },
                ["instruction_blocks"] = new JsonArray(),
                ["capabilities"] = new JsonArray(),
                ["runtime_context"] = new JsonObject
                {
                    ["mood"] = null,
                    ["hardware"] = null,
                    ["references"] = new JsonArray(),
                },
            },
            ["settings"] = new JsonObject(),
        };
    }
}
