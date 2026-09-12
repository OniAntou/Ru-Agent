using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class SystemPromptContractTests
{
    [Fact]
    public void Context_exposes_bounded_immutable_values()
    {
        var context = PromptFixtures.ValidContext();

        Assert.Equal(1, context.SchemaVersion);
        Assert.Equal("ru-agent", context.AgentProfile);
        Assert.Equal("standard", context.Preset);
        Assert.Single(context.InstructionBlocks);
        Assert.Equal("root:Ru.md", context.InstructionBlocks[0].SourceId);
        Assert.Equal(PromptInstructionOrigin.Workspace, context.InstructionBlocks[0].Origin);
        Assert.Contains("\"origin\":\"Workspace\"", Encoding.UTF8.GetString(context.SerializeUtf8()), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            PromptFixtures.ContextWithInstructionBytes(64 * 1024 + 1));
    }

    [Fact]
    public void Receipt_rejects_invalid_digest_and_result_keeps_step_pure()
    {
        var context = PromptFixtures.ValidContext();
        Assert.Throws<ArgumentException>(() =>
            PromptFixtures.Receipt("not-a-sha256", Array.Empty<PromptBlockDigest>()));

        var step = new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);
        var receipt = PromptFixtures.ValidReceipt(context);
        var result = new ModelStepResult(step, receipt);

        Assert.Same(step, result.Step);
        Assert.Same(receipt, result.PromptReceipt);
        Assert.DoesNotContain("prompt", typeof(AssistantStep).GetProperties().Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Instruction_block_rejects_undefined_origin_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PromptInstructionBlock(
                "skill:calendar",
                "Calendar helper",
                "External skill guidance.",
                "c60132ed4bbfd3d7b098b88390308e45dba05d846c3f3a1bcb8fe271042c4269",
                (PromptInstructionOrigin)999));
    }

    private static class PromptFixtures
    {
        public static SystemPromptContext ValidContext() =>
            new(
                1,
                "ru-agent",
                "standard",
                new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-08-29", "LLM"),
                new[]
                {
                    new PromptInstructionBlock(
                        "root:Ru.md",
                        "Ru.md",
                        "root guidance",
                        "9be3ca7b30ed9ac2b18b1c72ac9562e051667e37f4dcb5748bf802886acfb8e0"),
                },
                new[]
                {
                    new PromptCapability("files.read", "1.0.0", "Read a file", "workspace.read", "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}", "a".PadRight(64, 'a'), null, "enabled"),
                },
                new PromptRuntimeContext(null, null, null, null, Array.Empty<PromptReference>()));

        public static SystemPromptContext ContextWithInstructionBytes(int bytes)
        {
            var content = new string('x', bytes);
            return new SystemPromptContext(
                1,
                "ru-agent",
                "standard",
                new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-08-29", "LLM"),
                new[] { new PromptInstructionBlock("root:Ru.md", "Ru.md", content, "a".PadRight(64, 'a')) },
                Array.Empty<PromptCapability>(),
                new PromptRuntimeContext(null, null, null, null, Array.Empty<PromptReference>()));
        }

        public static SystemPromptReceipt Receipt(string promptSha256, IReadOnlyList<PromptBlockDigest> blocks) =>
            new("1.0.0", promptSha256, blocks);

        public static SystemPromptReceipt ValidReceipt(SystemPromptContext context) =>
            new(
                "1.0.0",
                "b".PadRight(64, 'b'),
                new[]
                {
                    new PromptBlockDigest("ru.persona", "b".PadRight(64, 'b')),
                    new PromptBlockDigest("agent.contract", "b".PadRight(64, 'b')),
                    new PromptBlockDigest("environment", "b".PadRight(64, 'b')),
                    new PromptBlockDigest("instruction.root:Ru.md", context.InstructionBlocks[0].ContentSha256),
                    new PromptBlockDigest("profile", "b".PadRight(64, 'b')),
                    new PromptBlockDigest("capabilities", "b".PadRight(64, 'b')),
                    new PromptBlockDigest("runtime", "b".PadRight(64, 'b')),
                });
    }
}
