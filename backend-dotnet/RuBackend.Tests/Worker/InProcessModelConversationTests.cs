using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class InProcessModelConversationTests
{
    [Fact]
    public void Anthropic_native_history_keeps_signed_blocks_order_and_pairs_results_with_is_error()
    {
        var arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement.Clone();
        var secondArguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"b.txt\"}").RootElement.Clone();
        var turns = new[]
        {
            new ModelTurn("assistant", "Reading both files.",
            [
                new ModelFunctionCall("toolu_read_a", "read", arguments),
                new ModelFunctionCall("toolu_read_b", "read", secondArguments),
            ]),
            new ModelTurn("tool", "first result", toolCallId: "toolu_read_a", toolResultCode: "read_failed"),
            new ModelTurn("tool", "second result", toolCallId: "toolu_read_b", toolResultCode: "ok"),
        };
        var continuation = new ProviderContinuation("anthropic", "anthropic_messages", "claude-fable-5", 1,
        [
            new JsonObject { ["type"] = "text", ["text"] = "Reading both files." },
            new JsonObject { ["type"] = "thinking", ["thinking"] = "private", ["signature"] = "signed-by-provider" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_read_a", ["name"] = "read", ["input"] = JsonNode.Parse("{\"path\":\"a.txt\"}") },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_read_b", ["name"] = "read", ["input"] = JsonNode.Parse("{\"path\":\"b.txt\"}") },
        ]);

        var messages = AnthropicMessagesHistoryProjector.Project(turns, [continuation], "claude-fable-5");

        Assert.Equal(2, messages.Count);
        Assert.Equal("assistant", messages[0]["role"]!.GetValue<string>());
        var assistantBlocks = messages[0]["content"]!.AsArray();
        Assert.Equal(["text", "thinking", "tool_use", "tool_use"], assistantBlocks.Select(block => block!["type"]!.GetValue<string>()));
        Assert.Equal("signed-by-provider", assistantBlocks[1]!["signature"]!.GetValue<string>());
        Assert.Equal("user", messages[1]["role"]!.GetValue<string>());
        var results = messages[1]["content"]!.AsArray();
        Assert.Equal(["toolu_read_a", "toolu_read_b"], results.Select(result => result!["tool_use_id"]!.GetValue<string>()));
        Assert.True(results[0]!["is_error"]!.GetValue<bool>());
        Assert.False(results[1]!["is_error"]!.GetValue<bool>());
        Assert.Equal(1, messages.Count(message => message["role"]!.GetValue<string>() == "assistant"));
    }

    [Fact]
    public void Responses_projection_replays_opaque_output_and_call_bound_result_without_merging_json_into_narration()
    {
        var continuation = new ProviderContinuation("openai", "openai_responses", "gpt-5.6-luna", 1,
        [
            new JsonObject { ["type"] = "reasoning", ["id"] = "reason-1", ["encrypted_content"] = "signed-bytes", ["summary"] = new JsonArray() },
            new JsonObject { ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "provider-1", ["name"] = "files.read", ["arguments"] = "{\"path\":\"a.txt\"}" },
        ]);
        var turns = new[]
        {
            new ModelTurn("assistant", "Reading the file.", [new ModelFunctionCall("provider-1", "files.read", Json("{\"path\":\"a.txt\"}"))]),
            new ModelTurn("tool", "file contents", toolCallId: "provider-1", toolResultCode: "ok"),
        };

        var items = OpenAiResponsesHistoryProjector.Project(turns, [continuation]);

        Assert.Equal("Reading the file.", items[0]!["content"]!.GetValue<string>());
        Assert.Equal("reasoning", items[1]!["type"]!.GetValue<string>());
        Assert.Equal("signed-bytes", items[1]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("provider-1", items[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("provider-1", items[3]!["call_id"]!.GetValue<string>());
        Assert.Equal("file contents", items[3]!["output"]!.GetValue<string>());
        Assert.DoesNotContain("path", items[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        var mismatched = new ProviderContinuation("openai", "openai_responses", "gpt-5.6-luna", 1,
        [
            new JsonObject { ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "provider-1", ["name"] = "files.read", ["arguments"] = "{\"path\":\"b.txt\"}" },
        ]);
        Assert.Throws<WorkerProtocolException>(() => OpenAiResponsesHistoryProjector.Project(turns, [mismatched]));
    }

    [Fact]
    public void First_step_uses_typed_messages_without_history()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "Tao file robots.txt roi tra loi DONE."));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Tao file robots.txt roi tra loi DONE.", prompt);
        Assert.Null(turns);
        Assert.Empty(messages!);
    }

    [Fact]
    public void Native_tool_exchange_uses_assistant_and_tool_messages()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "Tao file robots.txt."),
            AssistantStepEntry("src-2", 2, "provider_write_1", "write", "{\"op\":\"write\",\"path\":\"robots.txt\"}"),
            ToolResultEntry("src-3", 3, "internal_call_1", "provider_write_1", "{\"status\":\"ok\",\"path\":\"robots.txt\",\"bytes\":40}"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.Equal(3, messages!.Count);
        Assert.Equal("user", messages[0].Role);
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.Role);
        var call = Assert.Single(assistant.ToolCalls);
        Assert.Equal("provider_write_1", call.Id);
        Assert.Equal("write", call.Name);
        Assert.Equal("robots.txt", call.Arguments.GetProperty("path").GetString());
        var tool = messages[2];
        Assert.Equal("tool", tool.Role);
        Assert.Equal("provider_write_1", tool.ToolCallId);
        Assert.Equal("ok", tool.ToolResultCode);
        Assert.Equal("{\"status\":\"ok\",\"path\":\"robots.txt\",\"bytes\":40}", tool.Content);
        Assert.DoesNotContain("\\\"tool_id\\\"", assistant.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_tool_results_preserve_call_and_receipt_order()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc hai file"),
            AssistantCallsEntry(
                "src-2",
                2,
                [
                    new ModelToolCall("provider_read_a", "read", "1.0.0", Json("{\"path\":\"a.txt\"}")),
                    new ModelToolCall("provider_read_b", "read", "1.0.0", Json("{\"path\":\"b.txt\"}")),
                ]),
            ToolResultEntry("src-3", 3, "internal_call_a", "provider_read_a", "{\"path\":\"a.txt\",\"content\":\"aaa\"}"),
            ToolResultEntry("src-4", 4, "internal_call_b", "provider_read_b", "{\"path\":\"b.txt\",\"content\":\"bbb\"}"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.Equal(4, messages!.Count);
        Assert.Equal(["provider_read_a", "provider_read_b"], messages[1].ToolCalls.Select(item => item.Id));
        Assert.Equal(["provider_read_a", "provider_read_b"], messages.Skip(2).Select(item => item.ToolCallId));
    }

    [Fact]
    public void Exact_provider_receipts_reject_out_of_order_delivery()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc hai file"),
            AssistantCallsEntry(
                "src-2",
                2,
                [
                    new ModelToolCall("provider_read_a", "read", "1.0.0", Json("{\"path\":\"a.txt\"}")),
                    new ModelToolCall("provider_read_b", "read", "1.0.0", Json("{\"path\":\"b.txt\"}")),
                ]),
            ToolResultEntry("src-3", 3, "internal_call_a", "provider_read_b", "{\"path\":\"a.txt\"}"),
            ToolResultEntry("src-4", 4, "internal_call_b", "provider_read_a", "{\"path\":\"b.txt\"}"));

        var error = Assert.Throws<WorkerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(context));

        Assert.Equal("model_history_invalid", error.Code);
    }

    [Fact]
    public void Compacted_summary_is_a_user_message_and_keeps_final_prompt()
    {
        var context = Snapshot(
            ModelContextEntry.Summary(
                "cmp_1",
                1,
                "previously we wrote robots.txt",
                [new ContextEntryRange("history-a", 1, 1)]),
            ModelContextEntry.Text("src-2", 2, ModelContextRole.User, "tiep tuc"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Contains("previously we wrote robots.txt", prompt, StringComparison.Ordinal);
        Assert.Contains("tiep tuc", prompt, StringComparison.Ordinal);
        Assert.Null(turns);
        var summary = Assert.Single(messages!);
        Assert.Equal("user", summary.Role);
        Assert.Equal("compacted history summary:\npreviously we wrote robots.txt", summary.Content);
    }

    [Fact]
    public void User_summary_assistant_sequence_is_preserved()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "first request"),
            ModelContextEntry.Summary(
                "cmp-1",
                2,
                "compacted context",
                [new ContextEntryRange("history-a", 1, 1)]),
            ModelContextEntry.Assistant(
                "src-3",
                3,
                new AssistantStep("answer", [], AssistantFinishReason.Stop, 2)));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.NotNull(messages);
        Assert.Equal(["user", "user", "assistant"], messages.Select(message => message.Role));
        Assert.Equal("first request", messages[0].Content);
        Assert.Contains("compacted context", messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("answer", messages[2].Content);
    }

    [Fact]
    public void Assistant_text_after_tool_results_closes_the_native_batch()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc file"),
            AssistantStepEntry("src-2", 2, "provider_read_1", "read", "{\"path\":\"a.txt\"}"),
            ToolResultEntry("src-3", 3, "internal_call_1", "provider_read_1", "{\"path\":\"a.txt\",\"content\":\"aaa\"}"),
            ModelContextEntry.Assistant(
                "src-4",
                4,
                new AssistantStep("Da doc xong.", [], AssistantFinishReason.Stop, 8)));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("doc file", prompt);
        Assert.Null(turns);
        Assert.NotNull(messages);
        Assert.Equal(["user", "assistant", "tool", "assistant"], messages.Select(item => item.Role));
        Assert.Equal("Da doc xong.", messages[3].Content);
    }

    [Fact]
    public void Oversized_tool_output_is_clamped_without_becoming_the_prompt()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc file lon"),
            AssistantStepEntry("src-2", 2, "provider_read_1", "read", "{\"path\":\"big.txt\"}"),
            ToolResultEntry(
                "src-3",
                3,
                "internal_call_1",
                "provider_read_1",
                "{\"path\":\"big.txt\",\"content\":\"" + new string('x', 40_000) + "\"}"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.NotNull(messages);
        Assert.True(messages[2].Content.Length <= 6_000, $"tool content length was {messages[2].Content.Length}");
        Assert.Contains("truncated", messages[2].Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ignored_noop_entries_after_tool_results_keep_continuation_prompt(bool assistantNoop)
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc file"),
            AssistantStepEntry("src-2", 2, "provider_read_1", "read", "{\"path\":\"a.txt\"}"),
            ToolResultEntry("src-3", 3, "internal_call_1", "provider_read_1", "result"),
        };
        entries.Add(assistantNoop
            ? ModelContextEntry.Assistant(
                "src-4",
                4,
                new AssistantStep(null, [], AssistantFinishReason.Stop, 0))
            : ModelContextEntry.Text("src-4", 4, ModelContextRole.User, ""));

        var (prompt, _, messages) = InProcessModelStepRunner.BuildConversationForTests(Snapshot(entries.ToArray()));

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Equal("tool", messages![^1].Role);
    }

    [Fact]
    public void Native_history_budget_counts_exact_array_bytes_at_limit_and_over()
    {
        var emptyMessages = new ModelTurn[]
        {
            new("assistant", "", [new ModelFunctionCall("provider_read_1", "read", Json("{}"))]),
            new("tool", "r", toolCallId: "provider_read_1", toolResultCode: "ok"),
        };
        const int limit = 512 * 1024;
        var contentLength = limit - (int)ModelRequest.MeasureModelMessagesBytes(emptyMessages);
        var context = Snapshot(
            ModelContextEntry.Assistant(
                "src-1",
                1,
                new AssistantStep(
                    new string('a', contentLength),
                    [new ModelToolCall("provider_read_1", "read", "1.0.0", Json("{}"))],
                    AssistantFinishReason.ToolCalls,
                    1)),
            ToolResultEntry("src-2", 2, "internal_call_1", "provider_read_1", "r"));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context, hasImages: true);
        Assert.Equal(2, messages!.Count);
        Assert.Equal(limit, ModelRequest.MeasureModelMessagesBytes(messages));

        var oversized = Snapshot(
            ModelContextEntry.Assistant(
                "src-1",
                1,
                new AssistantStep(
                    new string('a', contentLength + 1),
                    [new ModelToolCall("provider_read_1", "read", "1.0.0", Json("{}"))],
                    AssistantFinishReason.ToolCalls,
                    1)),
            ToolResultEntry("src-2", 2, "internal_call_1", "provider_read_1", "r"));
        var error = Assert.Throws<WorkerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(oversized, hasImages: true));
        Assert.Equal("model_history_too_large", error.Code);
    }

    [Fact]
    public void Multi_message_history_uses_n_minus_one_separators_at_exact_budget()
    {
        const int limit = 512 * 1024;
        // A user message cannot be empty, so the baseline is measured with a
        // one-character placeholder and the old block is grown to fit the budget.
        var placeholder = Snapshot(
            ModelContextEntry.Text("old", 1, ModelContextRole.User, "o"),
            ModelContextEntry.Text("new", 2, ModelContextRole.User, "new"),
            ModelContextEntry.Assistant(
                "done",
                3,
                new AssistantStep("done", [], AssistantFinishReason.Stop, 0)));
        var (_, _, placeholderMessages) = InProcessModelStepRunner.BuildConversationForTests(placeholder);
        var oldLength = limit - (int)ModelRequest.MeasureModelMessagesBytes(placeholderMessages!) + 1;

        var exact = Snapshot(
            ModelContextEntry.Text("old", 1, ModelContextRole.User, new string('o', oldLength)),
            ModelContextEntry.Text("new", 2, ModelContextRole.User, "new"),
            ModelContextEntry.Assistant(
                "done",
                3,
                new AssistantStep("done", [], AssistantFinishReason.Stop, 0)));
        var (_, _, exactMessages) = InProcessModelStepRunner.BuildConversationForTests(exact);

        Assert.Equal(3, exactMessages!.Count);
        Assert.Equal(limit, ModelRequest.MeasureModelMessagesBytes(exactMessages));

        var oversized = Snapshot(
            ModelContextEntry.Text("old", 1, ModelContextRole.User, new string('o', oldLength + 1)),
            ModelContextEntry.Text("new", 2, ModelContextRole.User, "new"),
            ModelContextEntry.Assistant(
                "done",
                3,
                new AssistantStep("done", [], AssistantFinishReason.Stop, 0)));
        var (_, _, trimmedMessages) = InProcessModelStepRunner.BuildConversationForTests(oversized);

        Assert.Equal(["user", "assistant"], trimmedMessages!.Select(message => message.Role));
        var retainedUser = trimmedMessages!.First(message => message.Role == "user");
        Assert.Equal("new", retainedUser.Content);
    }

    [Fact]
    public void Stale_native_history_is_trimmed_by_complete_batches()
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("src-0", 1, ModelContextRole.User, "nhiem vu goc"),
        };
        var sequence = 2L;
        for (var index = 0; index < 90; index++)
        {
            entries.Add(AssistantStepEntry(
                $"stale-a-{index}",
                sequence++,
                $"provider_stale_{index}",
                "read",
                "{\"path\":\"stale.txt\"}"));
            entries.Add(ToolResultEntry(
                $"stale-t-{index}",
                sequence++,
                $"internal_stale_{index}",
                $"provider_stale_{index}",
                $"{{\"stale\":{index},\"content\":\"{new string('s', 10_000)}\"}}"));
        }
        entries.Add(AssistantStepEntry("src-new-a", sequence++, "provider_fresh", "read", "{\"path\":\"fresh.txt\"}"));
        entries.Add(ToolResultEntry("src-new-t", sequence, "internal_fresh", "provider_fresh", "{\"path\":\"fresh.txt\",\"content\":\"ket qua moi nhat\"}"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(Snapshot(entries.ToArray()));

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.NotNull(messages);
        Assert.NotEmpty(messages);
        Assert.True(messages.Count <= 256, $"messages count was {messages.Count}");
        var lowerBoundBytes = messages.Sum(message =>
            Encoding.UTF8.GetByteCount(message.Content)
            + message.ToolCalls.Sum(call => Encoding.UTF8.GetByteCount(call.Arguments.GetRawText())));
        Assert.True(lowerBoundBytes <= 512 * 1024, $"history lower-bound bytes were {lowerBoundBytes}");
        Assert.True(messages.Count < 181, $"history was not trimmed: {messages.Count} messages");
        Assert.Equal("provider_fresh", messages[^2].ToolCalls.Single().Id);
        Assert.Equal("provider_fresh", messages[^1].ToolCallId);
    }

    [Fact]
    public void Null_provider_call_id_fails_closed_at_construction()
    {
        var error = Assert.Throws<ArgumentException>(() => AssistantStepEntry(
            "src-2",
            2,
            null!,
            "read",
            "{\"path\":\"a.txt\"}"));

        Assert.Equal("providerCallId", error.ParamName);
    }

    [Fact]
    public void Ambiguous_id_less_multi_call_cannot_enter_history()
    {
        var error = Assert.Throws<ArgumentException>(() => AssistantCallsEntry(
            "src-2",
            2,
            [
                new ModelToolCall(null!, "read", "1.0.0", Json("{\"path\":\"a.txt\"}")),
                new ModelToolCall(null!, "read", "1.0.0", Json("{\"path\":\"b.txt\"}")),
            ]));

        Assert.Equal("providerCallId", error.ParamName);
    }

    [Fact]
    public void Tool_result_without_a_preceding_call_fails_closed()
    {
        var context = Snapshot(ToolResultEntry("src-1", 1, "internal_call_1", "provider_orphan", "orphan"));

        var error = Assert.Throws<WorkerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(context));

        Assert.Equal("model_history_invalid", error.Code);
    }

    private static ModelContextSnapshot Snapshot(params ModelContextEntry[] entries) =>
        new(entries, Array.Empty<byte>());

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static ModelContextEntry AssistantStepEntry(
        string sourceId,
        long sequence,
        string providerCallId,
        string toolId,
        string argumentsJson) =>
        AssistantCallsEntry(
            sourceId,
            sequence,
            [new ModelToolCall(providerCallId, toolId, "1.0.0", Json(argumentsJson))]);

    private static ModelContextEntry AssistantCallsEntry(
        string sourceId,
        long sequence,
        IReadOnlyList<ModelToolCall> calls) =>
        ModelContextEntry.Assistant(
            sourceId,
            sequence,
            new AssistantStep(null, calls, AssistantFinishReason.ToolCalls, 16));

    private static ModelContextEntry ToolResultEntry(
        string sourceId,
        long sequence,
        string toolCallId,
        string providerCallId,
        string content) =>
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
}
