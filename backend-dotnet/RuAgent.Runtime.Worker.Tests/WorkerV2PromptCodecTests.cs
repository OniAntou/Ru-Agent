using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerV2PromptCodecTests
{
    [Fact]
    public void Context_round_trip_preserves_exact_capability_contract_json()
    {
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"}}}";
        var context = PromptFixture.Create(new PromptCapability(
            "files.read",
            "1.0.0",
            "Read a bounded file",
            "workspace.read",
            schema,
            PromptHashForTest.Sha256(schema),
            "{\"path\":\"README.md\"}",
            "enabled"));

        var payload = WorkerV2PromptCodec.WriteContext(context);
        var start = new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = payload,
            ["settings"] = new JsonObject(),
        };
        var decoded = WorkerV2PromptCodec.ReadStart(start).Context;

        Assert.Equal(context.Capabilities.Single(), decoded.Capabilities.Single());
        Assert.Equal(schema, decoded.Capabilities.Single().InputSchemaJson);
    }

    [Fact]
    public void Context_round_trip_preserves_external_instruction_origin()
    {
        const string content = "External skill guidance.";
        var context = new SystemPromptContext(
            1,
            "ru-agent",
            "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-02", "LLM"),
            [new PromptInstructionBlock(
                "skill:calendar",
                "Calendar helper",
                content,
                PromptHashForTest.Sha256(content),
                PromptInstructionOrigin.ExternalSkill)],
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));

        var payload = WorkerV2PromptCodec.WriteContext(context);
        Assert.Equal("ExternalSkill", payload["instruction_blocks"]!.AsArray()[0]!.AsObject()["origin"]!.GetValue<string>());

        var decoded = WorkerV2PromptCodec.ReadStart(new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = payload,
            ["settings"] = new JsonObject(),
        }).Context;

        Assert.Equal(PromptInstructionOrigin.ExternalSkill, decoded.InstructionBlocks.Single().Origin);
    }

    [Fact]
    public void Context_read_without_origin_defaults_to_workspace()
    {
        const string content = "Workspace guidance.";
        var context = new SystemPromptContext(
            1,
            "ru-agent",
            "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-02", "LLM"),
            [new PromptInstructionBlock(
                "root:Ru.md",
                "Ru.md",
                content,
                PromptHashForTest.Sha256(content))],
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));

        var payload = WorkerV2PromptCodec.WriteContext(context);
        payload["instruction_blocks"]!.AsArray()[0]!.AsObject().Remove("origin");

        var decoded = WorkerV2PromptCodec.ReadStart(new JsonObject
        {
            ["prompt"] = "hello",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = payload,
            ["settings"] = new JsonObject(),
        }).Context;

        Assert.Equal(PromptInstructionOrigin.Workspace, decoded.InstructionBlocks.Single().Origin);
    }

    [Fact]
    public void Context_rejects_capability_digest_drift_before_worker_execution()
    {
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}";
        var context = PromptFixture.Create(new PromptCapability(
            "files.read", "1.0.0", "Read", "workspace.read", schema,
            PromptHashForTest.Sha256(schema), null, "enabled"));
        var payload = WorkerV2PromptCodec.WriteContext(context);
        payload["capabilities"]!.AsArray()[0]!["input_schema_digest"] = new string('0', 64);

        var error = Assert.Throws<RuAgent.Protocol.AgentProtocolException>(() =>
            WorkerV2PromptCodec.ReadStart(new JsonObject
            {
                ["prompt"] = "hello",
                ["turns"] = new JsonArray(),
                ["system_prompt_context"] = payload,
                ["settings"] = new JsonObject(),
            }));

        Assert.Equal("invalid_request", error.Code);
    }

    private static class PromptFixture
    {
        public static SystemPromptContext Create(PromptCapability capability) => new(
            1,
            "ru-agent",
            "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-02", "LLM"),
            Array.Empty<PromptInstructionBlock>(),
            new[] { capability },
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));
    }

    private static class PromptHashForTest
    {
        public static string Sha256(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
