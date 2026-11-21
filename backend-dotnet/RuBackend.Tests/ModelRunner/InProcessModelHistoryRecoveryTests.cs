using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

/// <summary>
/// A stored history the Host cannot pair is Host input, not a provider failure the
/// user can act on. These tests pin the bounded repair: keep complete batches, drop
/// unpaired ones, and keep the dialogue when no tool structure can be kept.
/// </summary>
public sealed class InProcessModelHistoryRecoveryTests
{
    [Fact]
    public void Unpaired_stored_call_no_longer_fails_the_step()
    {
        var context = Snapshot(
            User("old-user", 1, "Read a file"),
            AssistantCalls("unpaired", 2, ("call_a", "read")),
            User("current-user", 3, "Continue this task"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForTests(context);

        Assert.Equal("Continue this task", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.DoesNotContain(messages, message => message.ToolCalls.Count > 0);
        Assert.Contains(messages, message => message.Role == "user" && message.Content == "Read a file");
    }

    [Fact]
    public void Duplicate_provider_call_id_across_batches_drops_only_the_later_batch()
    {
        var context = Snapshot(
            User("old-user", 1, "Read the file"),
            AssistantCalls("first-batch", 2, ("call_dup", "read")),
            Receipt("first-receipt", 3, "internal-first", "call_dup"),
            AssistantCalls("second-batch", 4, ("call_dup", "read")),
            Receipt("second-receipt", 5, "internal-second", "call_dup"),
            User("current-user", 6, "Continue this task"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForTests(context);

        Assert.Equal("Continue this task", prompt);
        Assert.Single(messages, message => message.Role == "tool");
        var call = Assert.Single(messages, message => message.ToolCalls.Count > 0);
        Assert.Equal("call_dup", Assert.Single(call.ToolCalls).Id);
    }

    [Fact]
    public void Orphan_receipt_without_its_call_is_dropped()
    {
        var context = Snapshot(
            User("old-user", 1, "Look at the result"),
            Receipt("orphan", 2, "internal-orphan", "call_orphan"),
            User("current-user", 3, "Continue this task"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForTests(context);

        Assert.Equal("Continue this task", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
    }

    [Fact]
    public void Text_only_fallback_keeps_dialogue_and_drops_tool_structure()
    {
        var entries = new[]
        {
            User("old-user", 1, "Earlier request"),
            ModelContextEntry.Assistant("answer", 2,
                new AssistantStep("Earlier answer", [], AssistantFinishReason.Stop, 1)),
            ModelContextEntry.Summary("summary", 3, "compacted earlier work",
                [new ContextEntryRange("old-user", 1, 1)]),
            AssistantCalls("unpaired", 4, ("call_a", "read")),
            Receipt("unpaired-receipt", 5, "internal-unpaired", "call_a"),
            User("current-user", 6, "New request"),
        };

        var (prompt, messages) = InProcessModelStepRunner.BuildTextOnlyConversationForTests(entries);

        Assert.Equal("New request", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.DoesNotContain(messages, message => message.ToolCalls.Count > 0);
        Assert.Contains(messages, message => message.Role == "user" && message.Content == "Earlier request");
        Assert.Contains(messages, message => message.Role == "assistant" && message.Content == "Earlier answer");
        Assert.Contains(messages, message => message.Content.Contains("compacted earlier work", StringComparison.Ordinal));
    }

    [Fact]
    public void Request_ceiling_failure_continues_on_text_only_dialogue()
    {
        var context = Snapshot(
            User("old-user", 1, "Earlier request"),
            ModelContextEntry.Assistant("answer", 2,
                new AssistantStep("Earlier answer", [], AssistantFinishReason.Stop, 1)),
            AssistantCalls("batch", 3, ("call_a", "read")),
            Receipt("batch-receipt", 4, "internal-batch", "call_a"),
            User("current-user", 5, "New request"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForCodeForTests(
            context, "request_too_large");

        Assert.Equal("New request", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.DoesNotContain(messages, message => message.ToolCalls.Count > 0);
        Assert.Contains(messages, message => message.Role == "assistant" && message.Content == "Earlier answer");
    }

    private static ModelContextSnapshot Snapshot(params ModelContextEntry[] entries) =>
        new(entries, Array.Empty<byte>());

    private static ModelContextEntry User(string sourceId, long sequence, string content) =>
        ModelContextEntry.Text(sourceId, sequence, ModelContextRole.User, content);

    private static ModelContextEntry AssistantCalls(
        string sourceId,
        long sequence,
        params (string ProviderCallId, string ToolId)[] calls) =>
        ModelContextEntry.Assistant(
            sourceId,
            sequence,
            new AssistantStep(
                null,
                calls.Select(call => new ModelToolCall(
                    call.ProviderCallId,
                    call.ToolId,
                    "1.0.0",
                    Json("{}"))).ToArray(),
                AssistantFinishReason.ToolCalls,
                16));

    private static ModelContextEntry Receipt(
        string sourceId,
        long sequence,
        string toolCallId,
        string providerCallId) =>
        ModelContextEntry.ToolResult(
            sourceId,
            sequence,
            new ToolExecutionReceipt(
                new ExecutionId("exec_" + new string('a', 32)),
                new ToolCallId("call_" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(toolCallId))).ToLowerInvariant()[..32]),
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                providerCallId: providerCallId),
            "result");

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
