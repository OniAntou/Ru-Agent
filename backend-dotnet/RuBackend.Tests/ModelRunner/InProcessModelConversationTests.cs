using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class InProcessModelConversationTests
{
    [Fact]
    public void Updating_workflow_state_keeps_the_retained_conversation_before_it_unchanged()
    {
        ModelContextSnapshot Context(string state) => Snapshot(
            ModelContextEntry.Text(SessionWorkState.ContextSourceId, 1, ModelContextRole.User, state),
            ModelContextEntry.Text("original-user", 2, ModelContextRole.User, "Original task"),
            ModelContextEntry.Assistant("earlier-answer", 3, new AssistantStep("Earlier answer", [], AssistantFinishReason.Stop, 1)),
            ModelContextEntry.Text("current-user", 4, ModelContextRole.User, "Continue this task"));
        var (beforePrompt, _, before) = InProcessModelStepRunner.BuildConversationForTests(Context("old workflow"));
        var (afterPrompt, _, after) = InProcessModelStepRunner.BuildConversationForTests(Context("new workflow"));

        Assert.Equal("Continue this task", beforePrompt);
        Assert.Equal(beforePrompt, afterPrompt);
        Assert.Equal(before!.Count, after!.Count);
        Assert.Equal(JsonSerializer.Serialize(before.Take(before.Count - 1)),
            JsonSerializer.Serialize(after.Take(after.Count - 1)));
        Assert.Equal("old workflow", before[^1].Content);
        Assert.Equal("new workflow", after[^1].Content);
    }

    [Fact]
    public void Late_workflow_state_does_not_replace_the_durable_tool_continuation_prompt()
    {
        var context = Snapshot(
            ModelContextEntry.Text(SessionWorkState.ContextSourceId, 1, ModelContextRole.User, "Current workflow context"),
            ModelContextEntry.Text("original-user", 2, ModelContextRole.User, "Read a file"),
            AssistantStepEntry("call", 3, "provider_read", "read", "{\"path\":\"file.txt\"}"),
            ToolResultEntry("result", 4, "internal_read", "provider_read", "file contents"));

        var (prompt, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Equal("Current workflow context", messages![^1].Content);
        Assert.Equal("tool", messages[^2].Role);
        Assert.Equal("provider_read", messages[^2].ToolCallId);
    }

    [Fact]
    public void Run_handoff_is_context_for_new_model_without_replaying_old_native_calls()
    {
        const string handoff = "[session_run_handoff untrusted=true] proposed tool=edit; prior call=old-call; result=unverified";
        var context = Snapshot(
            ModelContextEntry.Text(SessionRunHandoff.ContextSourceId, 1, ModelContextRole.User, handoff),
            ModelContextEntry.Text("event:current", 2, ModelContextRole.User, "Check the existing changes and continue."));
        var (prompt, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);
        Assert.Equal("Check the existing changes and continue.", prompt);
        var historical = Assert.Single(messages!);
        Assert.Equal("user", historical.Role);
        Assert.Equal(handoff, historical.Content);
        Assert.True(historical.ToolCalls is null || historical.ToolCalls.Count == 0);
        Assert.Null(historical.ToolCallId);
    }

    [Fact]
    public void Run_handoff_cannot_silently_disappear_when_old_history_is_trimmed()
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text(SessionRunHandoff.ContextSourceId, 1, ModelContextRole.User, "Important prior receipts"),
        };
        for (int index = 0; index < ModelInputLimits.MaxMessages + 1; index++)
            entries.Add(ModelContextEntry.Assistant("event:old" + index, index + 2,
                new AssistantStep("Earlier answer", [], AssistantFinishReason.Stop, 1)));
        entries.Add(ModelContextEntry.Text("event:current", entries.Count + 1, ModelContextRole.User, "Continue"));
        var error = Assert.Throws<ModelRunnerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(Snapshot(entries.ToArray())));
        Assert.Equal("request_too_large", error.Code);
    }

    [Fact]
    public void Large_read_page_is_projected_as_valid_json_with_a_source_cursor()
    {
        var source = new JsonObject { ["path"] = "large.txt", ["offset"] = 0,
            ["content"] = new string('a', 1024 * 1024), ["bytes_read"] = 1024 * 1024,
            ["truncated"] = false }.ToJsonString();
        var context = Snapshot(ModelContextEntry.Text("user", 1, ModelContextRole.User, "Read the file"),
            AssistantStepEntry("call", 2, "provider_large", "read", "{\"path\":\"large.txt\"}"),
            ToolResultEntry("result", 3, "internal_large", "provider_large", source));
        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);
        using var json = JsonDocument.Parse(messages![^1].Content);
        var root = json.RootElement;
        Assert.True(root.GetProperty("projection_has_more").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(root.GetProperty("content").GetString()!),
            root.GetProperty("projection_next_offset").GetInt32());
        Assert.Equal("provider_large", messages[^1].ToolCallId);
    }

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

        var items = OpenAiResponsesHistoryProjector.Project(turns, [continuation], "gpt-5.6-luna");

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
        Assert.Throws<ModelRunnerProtocolException>(() => OpenAiResponsesHistoryProjector.Project(turns, [mismatched], "gpt-5.6-luna"));
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
    public void Workflow_state_is_a_separate_charged_turn_and_current_prompt_keeps_its_tail()
    {
        const string tail = "CURRENT-PROMPT-TAIL-8c1b";
        var workflowState = new string('s', 15_500);
        var currentPrompt = new string('p', 15_900 - tail.Length) + tail;
        var context = Snapshot(
            ModelContextEntry.Text(SessionWorkState.ContextSourceId, 1, ModelContextRole.User, workflowState),
            ModelContextEntry.Text("event:evt_" + new string('c', 32), 2, ModelContextRole.User, currentPrompt));

        var (prompt, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal(currentPrompt, prompt);
        var projectedMessages = messages!;
        var stateMessage = Assert.Single(projectedMessages, message => message.Content == workflowState);
        Assert.Equal("user", stateMessage.Role);
        Assert.DoesNotContain(projectedMessages, message => message.Content == currentPrompt);
    }

    [Fact]
    public void Oversized_current_prompt_fails_instead_of_prefix_clamping()
    {
        var prompt = new string('p', ModelInputLimits.MaxPromptChars + 1);
        var context = Snapshot(
            ModelContextEntry.Text("event:evt_" + new string('c', 32), 1, ModelContextRole.User, prompt));

        var error = Assert.Throws<ModelRunnerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(context));

        Assert.Equal("request_too_large", error.Code);
    }

    [Fact]
    public void Oversized_read_history_keeps_json_metadata_and_a_byte_cursor_for_visible_utf8()
    {
        const int sourceOffset = 17;
        const string hiddenMarker = "READ-CURSOR-MARKER-INSIDE-BROKER-PAGE";
        var content = "\uFEFF" + string.Concat(Enumerable.Repeat("Tiáº¿ngðŸ™‚", 14_000)) + hiddenMarker;
        var bytesRead = Encoding.UTF8.GetByteCount(content);
        var source = new JsonObject
        {
            ["path"] = "src/large.txt",
            ["content"] = content,
            ["truncated"] = true,
            ["bytes_read"] = bytesRead,
            ["total_bytes"] = sourceOffset + bytesRead + 512,
            ["offset"] = sourceOffset,
            ["sha256"] = new string('a', 64),
            ["duration_ms"] = 4,
        }.ToJsonString();
        var context = Snapshot(
            ModelContextEntry.Text("src-user", 1, ModelContextRole.User, "Find the end marker."),
            AssistantStepEntry("src-read", 2, "provider_read_1", "read",
                "{\"path\":\"src/large.txt\",\"offset\":17,\"limit\":65536}"),
            ToolResultEntry("src-result", 3, "internal_read_1", "provider_read_1", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        var result = messages![^1];
        using var projected = JsonDocument.Parse(result.Content);
        var root = projected.RootElement;
        var visible = root.GetProperty("content").GetString()!;
        Assert.Equal("src/large.txt", root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(bytesRead, root.GetProperty("bytes_read").GetInt32());
        Assert.Equal(sourceOffset + bytesRead + 512, root.GetProperty("total_bytes").GetInt32());
        Assert.Equal(sourceOffset, root.GetProperty("offset").GetInt32());
        Assert.Equal(new string('a', 64), root.GetProperty("sha256").GetString());
        Assert.DoesNotContain(hiddenMarker, visible, StringComparison.Ordinal);
        Assert.True(root.GetProperty("projection_has_more").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(visible), root.GetProperty("projection_visible_bytes").GetInt32());
        Assert.Equal(sourceOffset + Encoding.UTF8.GetByteCount(visible),
            root.GetProperty("projection_next_offset").GetInt32());
        Assert.Equal("provider_read_1", result.ToolCallId);
        Assert.Equal(source, context.Entries[^1].Content);
        Assert.DoesNotContain('\uFFFD', visible);
    }

    [Fact]
    public void Broker_truncated_read_page_exposes_cursor_even_when_projection_fits()
    {
        var source = new JsonObject
        {
            ["path"] = "src/large.txt",
            ["content"] = "abc",
            ["truncated"] = true,
            ["bytes_read"] = 3,
            ["total_bytes"] = 40,
            ["offset"] = 9,
            ["sha256"] = new string('b', 64),
        }.ToJsonString();
        var context = Snapshot(
            ModelContextEntry.Text("src-user", 1, ModelContextRole.User, "Continue reading."),
            AssistantStepEntry("src-read", 2, "provider_read_small", "read", "{\"path\":\"src/large.txt\",\"offset\":9}"),
            ToolResultEntry("src-result", 3, "internal_read_small", "provider_read_small", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        using var projected = JsonDocument.Parse(messages![^1].Content);
        Assert.True(projected.RootElement.GetProperty("projection_has_more").GetBoolean());
        Assert.Equal(12, projected.RootElement.GetProperty("projection_next_offset").GetInt32());
    }

    [Fact]
    public void Oversized_grep_history_resumes_from_original_offset_plus_visible_results()
    {
        const int sourceOffset = 37;
        const string hiddenMarker = "GREP-CURSOR-MARKER-INSIDE-BROKER-PAGE";
        var results = new JsonArray();
        for (var index = 0; index < 100; index++)
        {
            results.Add(new JsonObject
            {
                ["path"] = "docs/matches.txt",
                ["line"] = index + 1,
                ["text"] = index == 99 ? hiddenMarker : $"needle match detail {index:D3} {new string('界', 250)}",
            });
        }
        var source = new JsonObject
        {
            ["results"] = results,
            ["count"] = results.Count,
            ["truncated"] = true,
            ["scanned_files"] = 1,
            ["scanned_directories"] = 1,
            ["skipped"] = 0,
            ["duration_ms"] = 5,
        }.ToJsonString();
        var context = Snapshot(
            ModelContextEntry.Text("src-user", 1, ModelContextRole.User, "Find the final match."),
            AssistantStepEntry("src-grep", 2, "provider_grep_1", "grep",
                "{\"query\":\"needle\",\"path\":\"docs\",\"offset\":37,\"max_results\":100}"),
            ToolResultEntry("src-result", 3, "internal_grep_1", "provider_grep_1", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        var result = messages![^1];
        using var projected = JsonDocument.Parse(result.Content);
        var root = projected.RootElement;
        var visibleResults = root.GetProperty("results");
        Assert.Equal(100, root.GetProperty("count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.InRange(visibleResults.GetArrayLength(), 1, 99);
        Assert.DoesNotContain(hiddenMarker, result.Content, StringComparison.Ordinal);
        Assert.True(root.GetProperty("projection_has_more").GetBoolean());
        Assert.Equal(sourceOffset + visibleResults.GetArrayLength(),
            root.GetProperty("projection_next_offset").GetInt32());
        Assert.Equal("provider_grep_1", result.ToolCallId);
        Assert.Equal(source, context.Entries[^1].Content);
    }

    [Fact]
    public void Oversized_grep_without_optional_offset_uses_broker_default_zero()
    {
        var results = new JsonArray();
        for (var index = 0; index < 100; index++)
            results.Add(new JsonObject { ["preview"] = new string('x', 100), ["line"] = index + 1 });
        var source = new JsonObject { ["results"] = results, ["count"] = 100, ["truncated"] = true }.ToJsonString();
        var context = Snapshot(
            ModelContextEntry.Text("src-user", 1, ModelContextRole.User, "Find matches."),
            AssistantStepEntry("src-grep", 2, "provider_grep_default", "grep", "{\"query\":\"needle\",\"path\":\".\"}"),
            ToolResultEntry("src-result", 3, "internal_grep_default", "provider_grep_default", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        using var projected = JsonDocument.Parse(messages![^1].Content);
        var root = projected.RootElement;
        Assert.Equal(root.GetProperty("results").GetArrayLength(), root.GetProperty("projection_next_offset").GetInt32());
        Assert.True(root.GetProperty("projection_has_more").GetBoolean());
    }

    [Fact]
    public void Oversized_read_output_history_uses_utf16_cursor_without_splitting_surrogates()
    {
        const int sourceOffset = 120;
        const string hiddenMarker = "SPOOL-CURSOR-MARKER-INSIDE-BROKER-PAGE";
        var page = new string('s', ModelInputLimits.MaxToolResultChars - 120) + "ðŸ™‚" + new string('t', 300) + hiddenMarker;
        var source = new JsonObject
        {
            ["spool_id"] = "execution_0123456789abcdef0123456789abcdef",
            ["offset"] = sourceOffset,
            ["limit"] = 31_000,
            ["total_chars"] = 600_000,
            ["page"] = page,
            ["truncated"] = true,
        }.ToJsonString();
        var context = Snapshot(
            ModelContextEntry.Text("src-user", 1, ModelContextRole.User, "Find the middle output marker."),
            AssistantStepEntry("src-output", 2, "provider_output_1", "bash",
                "{\"mode\":\"read_output\",\"execution_id\":\"execution_0123456789abcdef0123456789abcdef\",\"offset\":120,\"limit\":7000}"),
            ToolResultEntry("src-result", 3, "internal_output_1", "provider_output_1", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        var result = messages![^1];
        using var projected = JsonDocument.Parse(result.Content);
        var root = projected.RootElement;
        var visible = root.GetProperty("page").GetString()!;
        _ = new UTF8Encoding(false, true).GetBytes(visible);
        Assert.Equal("execution_0123456789abcdef0123456789abcdef", root.GetProperty("spool_id").GetString());
        Assert.Equal(sourceOffset, root.GetProperty("offset").GetInt32());
        Assert.Equal(600_000, root.GetProperty("total_chars").GetInt32());
        Assert.DoesNotContain(hiddenMarker, visible, StringComparison.Ordinal);
        Assert.True(root.GetProperty("projection_has_more").GetBoolean());
        Assert.Equal(sourceOffset + visible.Length, root.GetProperty("projection_next_offset").GetInt32());
        Assert.Equal("provider_output_1", result.ToolCallId);
        Assert.Equal(source, context.Entries[^1].Content);
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
            ToolResultEntry("src-3", 3, "internal_call_a", "provider_read_a", "{\"path\":\"a.txt\",\"content\":\"aaa\",\"offset\":0}"),
            ToolResultEntry("src-4", 4, "internal_call_b", "provider_read_b", "{\"path\":\"b.txt\",\"content\":\"bbb\",\"offset\":0}"));

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

        var error = Assert.Throws<ModelRunnerProtocolException>(() =>
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

        Assert.Equal("tiep tuc", prompt);
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
            ToolResultEntry("src-3", 3, "internal_call_1", "provider_read_1", "{\"path\":\"a.txt\",\"content\":\"aaa\",\"offset\":0}"),
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
            AssistantStepEntry("src-2", 2, "provider_read_1", "unknown_tool", "{}"),
            ToolResultEntry(
                "src-3",
                3,
                "internal_call_1",
                "provider_read_1",
                "{\"unknown\":\"" + new string('x', ModelInputLimits.MaxToolResultChars + 40_000)));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.NotNull(messages);
        Assert.True(messages[2].Content.Length <= ModelInputLimits.MaxToolResultChars, $"tool content length was {messages[2].Content.Length}");
        Assert.Contains("truncated", messages[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_unknown_tool_history_keeps_the_full_block_cap_and_truthful_omission_count()
    {
        const string prefix = "{\"unknown\":\"";
        var source = prefix + string.Concat(Enumerable.Repeat("ðŸ™‚", 100_000));
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "inspect output"),
            AssistantStepEntry("src-2", 2, "provider_unknown_1", "unknown_tool", "{}"),
            ToolResultEntry("src-3", 3, "internal_unknown_1", "provider_unknown_1", source));

        var (_, _, messages) = InProcessModelStepRunner.BuildConversationForTests(context);

        var visible = Assert.Single(messages!, message => message.Role == "tool").Content;
        const string note = "\nâ€¦(truncated at conversation block limit; inspect the untrusted tool reference and continue at projection_next_offset when present)";
        Assert.InRange(visible.Length, 1, ModelInputLimits.MaxToolResultChars);
        var noteOffset = visible.IndexOf(note, StringComparison.Ordinal);
        Assert.True(noteOffset >= 0);
        var omitted = source.Length - noteOffset;
        Assert.EndsWith($" (omitted {omitted} of {source.Length} characters)", visible, StringComparison.Ordinal);
        _ = new UTF8Encoding(false, true).GetBytes(visible);
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
        const int resultSize = 130_000;
        var calls = Enumerable.Range(0, 64).Select(index => new ModelToolCall(
            "provider_" + index, "unknown", "1.0.0", Json("{}"))).ToArray();
        var messages = new List<ModelTurn> { new("assistant", "", calls.Select(call =>
            new ModelFunctionCall(call.ProviderCallId, call.ToolId, call.Arguments)).ToArray()) };
        messages.AddRange(calls.Select(call => new ModelTurn("tool", new string('r', resultSize),
            toolCallId: call.ProviderCallId, toolResultCode: "ok")));
        var narration = ModelInputLimits.MaxHistoryBytes - (int)ModelRequest.MeasureModelMessagesBytes(messages);
        ModelContextSnapshot Build(int extra)
        {
            var entries = new List<ModelContextEntry> { ModelContextEntry.Assistant("batch", 1,
                new AssistantStep(new string('a', narration + extra), calls, AssistantFinishReason.ToolCalls, 1)) };
            entries.AddRange(calls.Select((call, index) => ToolResultEntry("result-" + index, index + 2,
                "internal_" + index, call.ProviderCallId, new string('r', resultSize))));
            return Snapshot(entries.ToArray());
        }
        var (_, _, exact) = InProcessModelStepRunner.BuildConversationForTests(Build(0), hasImages: true);
        Assert.Equal(ModelInputLimits.MaxHistoryBytes, ModelRequest.MeasureModelMessagesBytes(exact!));
        var error = Assert.Throws<ModelRunnerProtocolException>(() =>
            InProcessModelStepRunner.BuildConversationForTests(Build(1), hasImages: true));
        Assert.Equal("model_history_too_large", error.Code);
    }

    [Fact]
    public void Multi_message_history_uses_n_minus_one_separators_at_exact_budget()
    {
        var placeholder = Enumerable.Range(0, 10).Select(index => ModelContextEntry.Text(
            "user-" + index, index + 1, ModelContextRole.User, "x")).ToList();
        placeholder.Add(ModelContextEntry.Assistant("done", 11, new AssistantStep("done", [], AssistantFinishReason.Stop, 0)));
        var (_, _, baseline) = InProcessModelStepRunner.BuildConversationForTests(Snapshot(placeholder.ToArray()));
        var remaining = ModelInputLimits.MaxHistoryBytes - (int)ModelRequest.MeasureModelMessagesBytes(baseline!);
        ModelContextSnapshot Build(int extra)
        {
            var entries = Enumerable.Range(0, 10).Select(index => ModelContextEntry.Text("user-"+index,
                index+1, ModelContextRole.User, new string('x', 1+remaining/10+(index<remaining%10?1:0)+(index==0?extra:0)))).ToList();
            entries.Add(placeholder[^1]);
            return Snapshot(entries.ToArray());
        }
        var (_, _, exact) = InProcessModelStepRunner.BuildConversationForTests(Build(0));
        Assert.Equal(11, exact!.Count);
        Assert.Equal(ModelInputLimits.MaxHistoryBytes, ModelRequest.MeasureModelMessagesBytes(exact));
        var (_, _, trimmed) = InProcessModelStepRunner.BuildConversationForTests(Build(1));
        Assert.Equal(10, trimmed!.Count);
        Assert.True(ModelRequest.MeasureModelMessagesBytes(trimmed) < ModelInputLimits.MaxHistoryBytes);
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
                $"{{\"stale\":{index},\"content\":\"{new string('s', 100_000)}\",\"offset\":0}}"));
        }
        entries.Add(AssistantStepEntry("src-new-a", sequence++, "provider_fresh", "read", "{\"path\":\"fresh.txt\"}"));
        entries.Add(ToolResultEntry("src-new-t", sequence, "internal_fresh", "provider_fresh", "{\"path\":\"fresh.txt\",\"content\":\"ket qua moi nhat\",\"offset\":0}"));

        var (prompt, turns, messages) = InProcessModelStepRunner.BuildConversationForTests(Snapshot(entries.ToArray()));

        Assert.Equal("Continue from the durable tool results.", prompt);
        Assert.Null(turns);
        Assert.NotNull(messages);
        Assert.NotEmpty(messages);
        Assert.True(messages.Count <= ModelInputLimits.MaxMessages, $"messages count was {messages.Count}");
        var lowerBoundBytes = messages.Sum(message =>
            Encoding.UTF8.GetByteCount(message.Content)
            + message.ToolCalls.Sum(call => Encoding.UTF8.GetByteCount(call.Arguments.GetRawText())));
        Assert.True(lowerBoundBytes <= ModelInputLimits.MaxHistoryBytes, $"history lower-bound bytes were {lowerBoundBytes}");
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

        var error = Assert.Throws<ModelRunnerProtocolException>(() =>
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
