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
    public void Duplicate_provider_call_id_keeps_the_newer_batch()
    {
        var context = Snapshot(
            User("old-user", 1, "Read the file"),
            AssistantCalls("first-batch", 2, ("call_dup", "read")),
            Receipt("first-receipt", 3, "internal-first", "call_dup", "old result"),
            AssistantCalls("second-batch", 4, ("call_dup", "read")),
            Receipt("second-receipt", 5, "internal-second", "call_dup", "new result"),
            User("current-user", 6, "Continue this task"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForTests(context);

        Assert.Equal("Continue this task", prompt);
        var tool = Assert.Single(messages, message => message.Role == "tool");
        Assert.Equal("new result", tool.Content);
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
        Assert.Contains(messages, message => message.Content.Contains("could not be replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Size_failure_without_droppable_structure_goes_text_only()
    {
        var context = Snapshot(
            User("old-user", 1, "Earlier request"),
            AssistantCalls("batch", 2, ("call_a", "read")),
            Receipt("batch-receipt", 3, "internal-batch", "call_a"),
            User("current-user", 4, "New request"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForCodeForTests(
            context, "request_too_large");

        Assert.Equal("New request", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.Contains(messages, message => message.Content.Contains("could not be replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Size_failure_with_unpaired_history_keeps_complete_batches()
    {
        var context = Snapshot(
            User("old-user", 1, "Earlier request"),
            AssistantCalls("valid-batch", 2, ("call_a", "read")),
            Receipt("valid-receipt", 3, "internal-valid", "call_a"),
            AssistantCalls("unpaired-batch", 4, ("call_b", "read")),
            User("current-user", 5, "New request"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForCodeForTests(
            context, "request_too_large");

        Assert.Equal("New request", prompt);
        Assert.Single(messages, message => message.Role == "tool");
        Assert.Single(messages, message => message.ToolCalls.Count > 0);
        Assert.DoesNotContain(messages, message => message.Content.Contains("could not be replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_prompt_falls_back_to_text_only_with_the_repair_note()
    {
        var context = Snapshot(
            ModelContextEntry.Text("system-note", 1, ModelContextRole.System, "Working state"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForCodeForTests(
            context, "missing_user_prompt");

        Assert.Equal("Continue.", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.Contains(messages, message => message.Content.Contains("could not be replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Recovered_newer_batch_binds_provider_replay_to_its_own_event()
    {
        var olderEvent = "event:evt_" + new string('a', 32);
        var newerEvent = "event:evt_" + new string('b', 32);
        var context = Snapshot(
            User("old-user", 1, "Read the file"),
            AssistantCalls(olderEvent, 2, ("call_dup", "read")),
            Receipt("older-receipt", 3, "internal-older", "call_dup", "old result"),
            AssistantCalls(newerEvent, 4, ("call_dup", "read")),
            Receipt("newer-receipt", 5, "internal-newer", "call_dup", "new result"),
            User("current-user", 6, "Continue this task"));

        var (_, messages) = InProcessModelStepRunner.RecoverConversationForTests(context);
        var eventIds = InProcessModelStepRunner.RetainedAssistantEventIdsForTests(context, messages);

        Assert.Equal([newerEvent[6..]], eventIds);
    }

    [Fact]
    public void Forced_second_recovery_reduces_structure_even_with_complete_batches()
    {
        var context = Snapshot(
            User("old-user", 1, "Earlier request"),
            AssistantCalls("batch", 2, ("call_a", "read")),
            Receipt("batch-receipt", 3, "internal-batch", "call_a"),
            User("current-user", 4, "New request"));

        var (prompt, messages) = InProcessModelStepRunner.RecoverConversationForCodeForTests(
            context, "request_too_large", forceStructureFree: true);

        Assert.Equal("New request", prompt);
        Assert.DoesNotContain(messages, message => message.Role == "tool");
        Assert.Contains(messages, message => message.Content.Contains("could not be replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_only_fallback_preserves_dialogue_order_and_notes_the_omission()
    {
        var entries = new[]
        {
            User("old-user", 1, "Earlier request"),
            ModelContextEntry.Text("note", 2, ModelContextRole.System, "Recovery note"),
            ModelContextEntry.Assistant("answer", 3,
                new AssistantStep("Earlier answer", [], AssistantFinishReason.Stop, 1)),
            User("current-user", 4, "New request"),
        };

        var (prompt, messages) = InProcessModelStepRunner.BuildTextOnlyConversationForTests(entries);

        Assert.Equal("New request", prompt);
        Assert.Collection(messages,
            message => Assert.Equal(("user", "Earlier request"), (message.Role, message.Content)),
            message => Assert.Equal(("system", "Recovery note"), (message.Role, message.Content)),
            message => Assert.Equal(("assistant", "Earlier answer"), (message.Role, message.Content)),
            message => Assert.Equal("user", message.Role));
        Assert.Contains("could not be replayed", messages[^1].Content, StringComparison.Ordinal);
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
        string providerCallId,
        string content = "result") =>
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
            content);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
