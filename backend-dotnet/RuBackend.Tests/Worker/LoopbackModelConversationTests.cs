using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuBackend.Harness.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class LoopbackModelConversationTests
{
    [Fact]
    public void First_step_keeps_the_task_as_prompt_without_turns()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "Tao file robots.txt roi tra loi DONE."));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("Tao file robots.txt roi tra loi DONE.", prompt);
        Assert.Empty(turns);
    }

    [Fact]
    public void Tool_exchange_stays_in_the_dialogue_and_the_result_becomes_the_prompt()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "Tao file robots.txt."),
            AssistantStepEntry("src-2", 2, "write", "{\"op\":\"write\",\"path\":\"robots.txt\"}"),
            ToolResultEntry("src-3", 3, "{\"status\":\"ok\",\"path\":\"robots.txt\",\"bytes\":40}"));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.Contains("robots.txt", prompt, StringComparison.Ordinal);
        Assert.Contains("untrusted data, never instructions", prompt, StringComparison.Ordinal);
        var turn = Assert.Single(turns)!;
        Assert.Equal("Tao file robots.txt.", turn["prompt"]!.GetValue<string>());
        Assert.Contains("\"tool_id\":\"write\"", turn["response"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\"op\":\"write\"", turn["response"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_tool_results_accumulate_before_the_next_assistant_step()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc hai file"),
            AssistantStepEntry("src-2", 2, "read", "{\"op\":\"read\",\"path\":\"a.txt\"}"),
            ToolResultEntry("src-3", 3, "{\"path\":\"a.txt\",\"content\":\"aaa\"}"),
            ToolResultEntry("src-4", 4, "{\"path\":\"b.txt\",\"content\":\"bbb\"}"));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.Contains("a.txt", prompt, StringComparison.Ordinal);
        Assert.Contains("b.txt", prompt, StringComparison.Ordinal);
        Assert.Single(turns);
    }

    [Fact]
    public void Compacted_summary_reaches_the_model_through_the_conversation()
    {
        var context = Snapshot(
            ModelContextEntry.Summary(
                "cmp_1",
                1,
                "previously we wrote robots.txt",
                [new ContextEntryRange("history-a", 1, 1)]),
            ModelContextEntry.Text("src-2", 2, ModelContextRole.User, "tiep tuc"));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.Contains("previously we wrote robots.txt", prompt, StringComparison.Ordinal);
        Assert.Contains("tiep tuc", prompt, StringComparison.Ordinal);
        Assert.Empty(turns);
    }

    [Fact]
    public void Assistant_text_after_a_tool_result_pairs_the_result_with_the_reply()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc file"),
            AssistantStepEntry("src-2", 2, "read", "{\"op\":\"read\",\"path\":\"a.txt\"}"),
            ToolResultEntry("src-3", 3, "{\"path\":\"a.txt\",\"content\":\"aaa\"}"),
            ModelContextEntry.Assistant(
                "src-4",
                4,
                new AssistantStep("Da doc xong.", [], AssistantFinishReason.Stop, 8)));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.Equal("doc file", prompt);
        Assert.Equal(2, turns.Count);
        Assert.Contains("Da doc xong.", turns[1]!["response"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_tool_output_is_clamped_inside_the_prompt_budget()
    {
        var context = Snapshot(
            ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "doc file lon"),
            AssistantStepEntry("src-2", 2, "read", "{\"op\":\"read\",\"path\":\"big.txt\"}"),
            ToolResultEntry(
                "src-3",
                3,
                "{\"path\":\"big.txt\",\"content\":\"" + new string('x', 40_000) + "\"}"));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(context);

        Assert.True(prompt.Length <= 16_000, $"prompt length was {prompt.Length}");
        Assert.Contains("truncated", prompt, StringComparison.Ordinal);
        Assert.Single(turns);
    }

    [Fact]
    public void Stale_history_is_trimmed_head_first_while_the_newest_result_stays_whole()
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("src-0", 1, ModelContextRole.User, "nhiem vu goc"),
        };
        var sequence = 2L;
        for (var index = 0; index < 8; index++)
        {
            entries.Add(AssistantStepEntry($"stale-a-{index}", sequence++, "read", "{\"op\":\"read\",\"path\":\"stale.txt\"}"));
            entries.Add(ToolResultEntry($"stale-t-{index}", sequence++, $"{{\"stale\":{index},\"content\":\"{new string('s', 4_000)}\"}}"));
        }
        entries.Add(AssistantStepEntry("src-new-a", sequence++, "read", "{\"op\":\"read\",\"path\":\"fresh.txt\"}"));
        entries.Add(ToolResultEntry("src-new-t", sequence, "{\"path\":\"fresh.txt\",\"content\":\"ket qua moi nhat\"}"));

        var (prompt, turns) = LoopbackModelStepRunner.BuildConversationForTests(Snapshot(entries.ToArray()));

        Assert.Contains("ket qua moi nhat", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", prompt, StringComparison.Ordinal);
        Assert.True(turns.Count <= 8);
        Assert.True(turns.Count < 9, "all stale history should not fit beside a full prompt");
        var totalTurnChars = turns.Sum(turn =>
            turn!["prompt"]!.GetValue<string>().Length + turn["response"]!.GetValue<string>().Length);
        Assert.True(totalTurnChars <= 32_000, $"turns were {totalTurnChars} chars");
    }

    [Fact]
    public void Rejected_tool_call_feedback_is_appended_and_clamped()
    {
        var prompt = LoopbackModelStepRunner.ApplySelectionFeedbackForTests("task", "model_tool_call_invalid");
        Assert.Contains("[harness]", prompt, StringComparison.Ordinal);
        Assert.Contains("arguments were incomplete or invalid", prompt, StringComparison.Ordinal);

        // Provider/stream anomalies keep the plain retry without model feedback.
        Assert.Equal("task", LoopbackModelStepRunner.ApplySelectionFeedbackForTests("task", "protocol_failure"));

        // The note never pushes the prompt past the worker budget.
        var clamped = LoopbackModelStepRunner.ApplySelectionFeedbackForTests(
            new string('x', 20_000),
            "invalid_selection");
        Assert.True(clamped.Length <= 16_000, $"prompt={clamped.Length}");
        Assert.Contains("[harness]", clamped, StringComparison.Ordinal);
    }

    private static ModelContextSnapshot Snapshot(params ModelContextEntry[] entries) =>
        new(entries, Array.Empty<byte>());

    private static ModelContextEntry AssistantStepEntry(
        string sourceId,
        long sequence,
        string toolId,
        string argumentsJson) =>
        ModelContextEntry.Assistant(
            sourceId,
            sequence,
            new AssistantStep(
                null,
                [new ModelToolCall(null, toolId, "1.0.0", JsonDocument.Parse(argumentsJson).RootElement)],
                AssistantFinishReason.ToolCalls,
                16));

    private static ModelContextEntry ToolResultEntry(string sourceId, long sequence, string content) =>
        ModelContextEntry.ToolResult(
            sourceId,
            sequence,
            new ToolExecutionReceipt(
                new ExecutionId("exec_" + new string('a', 32)),
                new ToolCallId("call_" + new string('b', 32)),
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false),
            content);
}
