using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class ConsecutiveToolCallGuardTests
{
    [Fact]
    public void Identical_completed_call_is_repeated_regardless_of_object_property_order()
    {
        var prior = Call("{\"path\":\"README.md\",\"offset\":0}");
        var proposed = Call("{\"offset\":0,\"path\":\"README.md\"}");

        Assert.True(ConsecutiveToolCallGuard.IsRepeated(Context(prior, includeReceipt: true), [proposed]));
    }

    [Fact]
    public void Changed_call_or_missing_terminal_receipt_is_not_repeated()
    {
        var prior = Call("{\"path\":\"README.md\"}");
        var changed = Call("{\"path\":\"docs/README.md\"}");

        Assert.False(ConsecutiveToolCallGuard.IsRepeated(Context(prior, includeReceipt: true), [changed]));
        Assert.False(ConsecutiveToolCallGuard.IsRepeated(Context(prior, includeReceipt: false), [prior]));
    }

    [Fact]
    public void Intervening_non_tool_entry_breaks_the_consecutive_chain()
    {
        var call = Call("{\"path\":\"README.md\"}");
        var entries = Context(call, includeReceipt: true).Entries
            .Append(ModelContextEntry.Text("user-next", 3, ModelContextRole.User, "continue"))
            .ToArray();
        var context = new ModelContextSnapshot(entries, "[]"u8.ToArray());

        Assert.False(ConsecutiveToolCallGuard.IsRepeated(context, [call]));
    }

    [Fact]
    public void Single_retry_after_never_executed_infra_failure_is_allowed()
    {
        var call = Call("{\"op\":\"list\",\"path\":\"D:/Test Agent\"}");
        var context = InfraFailureContext(call, "decision_stale");

        Assert.False(ConsecutiveToolCallGuard.IsRepeated(context, [call]));
    }

    [Fact]
    public void Second_consecutive_infra_failure_is_still_repeated()
    {
        var call = Call("{\"op\":\"list\",\"path\":\"D:/Test Agent\"}");
        var first = InfraFailureContext(call, "decision_stale").Entries.ToList();
        first.Add(ModelContextEntry.Assistant(
            "assistant-retry",
            3,
            new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)));
        first.Add(ToolResult("tool-retry", 4, "decision_stale"));
        var context = new ModelContextSnapshot(first, "[]"u8.ToArray());

        Assert.True(ConsecutiveToolCallGuard.IsRepeated(context, [call]));
    }

    [Fact]
    public void Retry_after_tool_level_denial_is_still_repeated()
    {
        var call = Call("{\"op\":\"list\",\"path\":\"D:/Test Agent\"}");
        var context = InfraFailureContext(call, "denied");

        Assert.True(ConsecutiveToolCallGuard.IsRepeated(context, [call]));
    }

    [Fact]
    public void Repetition_advises_while_the_repair_budget_lasts_then_stops()
    {
        var call = Call("{\"op\":\"list\",\"path\":\"D:/Test Agent\"}");
        var entries = InfraFailureContext(call, "malformed_call").Entries.ToList();

        var advised = ConsecutiveToolCallGuard.Inspect(
            new ModelContextSnapshot(entries, "[]"u8.ToArray()),
            [call],
            out var advisedCode);

        Assert.Equal(RepetitionGuardVerdict.Advise, advised);
        Assert.Equal("malformed_call", advisedCode);

        for (var index = 0; index < ConsecutiveToolCallGuard.MaximumRepairNotes; index++)
        {
            entries.Insert(
                0,
                ModelContextEntry.Text(
                    $"note-{index}",
                    index + 1,
                    ModelContextRole.System,
                    ConsecutiveToolCallGuard.BuildRepairNote(advisedCode)));
        }

        var stopped = ConsecutiveToolCallGuard.Inspect(
            new ModelContextSnapshot(entries, "[]"u8.ToArray()),
            [call],
            out var stopCode);

        Assert.Equal(RepetitionGuardVerdict.Stop, stopped);
        Assert.Equal("malformed_call", stopCode);
    }

    [Fact]
    public void Repair_note_names_the_rejection_code_behind_the_marker()
    {
        var note = ConsecutiveToolCallGuard.BuildRepairNote("malformed_call");

        Assert.StartsWith(ConsecutiveToolCallGuard.RepairNotePrefix, note, StringComparison.Ordinal);
        Assert.Contains("malformed_call", note, StringComparison.Ordinal);
        Assert.True(note.Length <= 512);
        Assert.StartsWith(
            ConsecutiveToolCallGuard.RepairNotePrefix,
            ConsecutiveToolCallGuard.BuildRepairNote(null),
            StringComparison.Ordinal);
    }

    private static ModelContextSnapshot Context(ModelToolCall call, bool includeReceipt)
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Assistant(
                "assistant-prior",
                1,
                new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)),
        };
        if (includeReceipt)
        {
            entries.Add(ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: false,
                providerCallId: "provider_call"),
                "{}"));
        }

        return new ModelContextSnapshot(entries, "[]"u8.ToArray());
    }

    private static ModelToolCall Call(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ModelToolCall("provider_call", "files.read", "1", document.RootElement);
    }

    private static ModelContextSnapshot InfraFailureContext(ModelToolCall call, string code)
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Assistant(
                "assistant-prior",
                1,
                new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)),
            ToolResult("tool-prior", 2, code),
        };
        return new ModelContextSnapshot(entries, "[]"u8.ToArray());
    }

    private static ModelContextEntry ToolResult(string sourceId, long sequence, string code) =>
        ModelContextEntry.ToolResult(
            sourceId,
            sequence,
            new ToolExecutionReceipt(
                new ExecutionId("exec_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                ToolExecutionOutcome.Failed,
                code,
                null,
                sideEffectsPossible: false,
                providerCallId: "provider_call"),
            "{}");
}
