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
                    false),
                "{}"));
        }

        return new ModelContextSnapshot(entries, "[]"u8.ToArray());
    }

    private static ModelToolCall Call(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ModelToolCall(null, "files.read", "1", document.RootElement);
    }
}
