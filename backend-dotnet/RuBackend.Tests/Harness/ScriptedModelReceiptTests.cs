using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The scripted model's prompt receipt must satisfy the coordinator that consumes it.
/// </summary>
/// <remarks>
/// Every model step is checked against the prompt context before it is accepted, so a
/// scripted step that composes a mismatched receipt fails the run as prompt_contract_invalid.
/// That happened only once a script had two tool steps in it, which is why the shape of the
/// context matters and is asserted here rather than left to the manual harness.
/// </remarks>
public sealed class ScriptedModelReceiptTests
{
    /// <summary>
    /// A prompt context shaped like the one a real run builds.
    /// </summary>
    /// <remarks>
    /// The block order and the instruction blocks are what the receipt is checked against,
    /// so a thin fixture would not exercise the check. A capability is included because it
    /// is the part of the prompt that differs between the two capabilities renderings.
    /// </remarks>
    private static SystemPromptContext Context()
    {
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}";
        const string instructions = "workspace instructions";
        return new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-18", "LLM"),
            [new PromptInstructionBlock(
                "RU.md",
                "RU.md",
                instructions,
                Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(instructions))))],
            [new PromptCapability("read", "1.0.0", "Read a file", "system.read", schema, new string('0', 64), null, "enabled")],
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));
    }

    [Fact]
    public void The_receipt_satisfies_the_coordinator_check()
    {
        var context = Context();
        var receipt = ScriptedModelStepRunner.ComposeReceiptForTests(context);

        // Throws when the block order, the digests or the composer version disagree with
        // the context, which is exactly what the coordinator does.
        receipt.ValidateAgainst(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Either_capabilities_rendering_is_accepted(bool nativeToolCalling)
    {
        // The Worker picks this from the provider's protocol, which a scripted model does
        // not have. Both renderings must therefore be usable, or a script is only valid for
        // one provider dialect.
        var context = Context();
        var receipt = SystemPromptComposer.Compose(context, nativeToolCalling).Receipt;

        receipt.ValidateAgainst(context);
    }

    [Fact]
    public void The_two_renderings_actually_differ()
    {
        // If they were identical this whole concern would be moot, and the test above would
        // pass without proving anything about the choice.
        var context = Context();

        var native = SystemPromptComposer.Compose(context, nativeToolCalling: true).Receipt;
        var text = SystemPromptComposer.Compose(context, nativeToolCalling: false).Receipt;

        Assert.NotEqual(native.PromptSha256, text.PromptSha256);
    }

    [Fact]
    public async Task Scripted_projection_cursor_placeholder_uses_the_latest_runtime_reference()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests("""
            [
              { "text": "Start deterministic trace." },
              { "tool": { "id": "read", "arguments": { "path": "large.txt", "offset": "$projection_next_offset" } } }
            ]
            """);
        var request = new ModelStepRequest(
            new RunId("run_11111111111111111111111111111111"),
            new ModelStepId("step_11111111111111111111111111111111"),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            ContextWithProjectionCursor(8_192),
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64));

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);
        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        var call = Assert.Single(result.Step.ToolCalls);
        Assert.Equal(8_192, call.Arguments.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task Scripted_paging_stops_at_complete_reference_and_uses_following_final_step()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests("""
            [
              { "tool": { "id": "grep", "arguments": { "query": "needle", "offset": "$projection_next_offset" } } },
              { "tool": { "id": "grep", "arguments": { "query": "needle", "offset": "$projection_next_offset" } } },
              { "text": "The deterministic page sequence ended." }
            ]
            """);
        var request = new ModelStepRequest(
            new RunId("run_11111111111111111111111111111111"),
            new ModelStepId("step_11111111111111111111111111111111"),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            ContextWithCompleteGrepReference(),
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64));

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Empty(result.Step.ToolCalls);
        Assert.Equal("The deterministic page sequence ended.", result.Step.Text);
    }

    private static SystemPromptContext ContextWithProjectionCursor(int cursor)
    {
        var context = Context();
        return new SystemPromptContext(
            context.SchemaVersion,
            context.AgentProfile,
            context.Preset,
            context.Environment,
            context.InstructionBlocks,
            context.Capabilities,
            new PromptRuntimeContext(null, null,
            [new PromptReference("tool_result", "untrusted tool result",
                $"{{\"projection_next_offset\":{cursor},\"projection_has_more\":true}}") ]));
    }

    private static SystemPromptContext ContextWithCompleteGrepReference()
    {
        var context = Context();
        return new SystemPromptContext(
            context.SchemaVersion,
            context.AgentProfile,
            context.Preset,
            context.Environment,
            context.InstructionBlocks,
            context.Capabilities,
            new PromptRuntimeContext(null, null,
            [new PromptReference("tool_result", "untrusted tool result",
                "{\"results\":[],\"count\":0,\"truncated\":false}") ]));
    }
}
