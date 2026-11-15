using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class VisibleReceiptEventTests
{
    [Fact]
    public void Only_tool_results_in_the_context_count_as_visible_receipts()
    {
        ModelContextEntry[] entries =
        [
            ModelContextEntry.Text("event:evt_11111111111111111111111111111111", 1, ModelContextRole.User, "prompt"),
            ToolResult("event:evt_22222222222222222222222222222222", 2),
            ToolResult("base:session-work-state", 3),
            ModelContextEntry.Text("runtime-snapshot:evt_33333333333333333333333333333333", 4, ModelContextRole.User, "snapshot"),
        ];

        var visible = SqliteModelContextSource.VisibleReceiptEventIds(entries);

        // A user message sourced from an event is not a receipt; only the tool result is.
        Assert.Equal(["evt_22222222222222222222222222222222"], visible);
    }

    [Fact]
    public void A_compacted_tool_result_is_no_longer_visible()
    {
        // Compaction replaces the covered entries with a summary, so their receipts drop out.
        var visible = SqliteModelContextSource.VisibleReceiptEventIds(
            [ModelContextEntry.Text("summary:one", 1, ModelContextRole.User, "summary of earlier work")]);

        Assert.Empty(visible);
    }

    private static ModelContextEntry ToolResult(string sourceId, long sequence) =>
        ModelContextEntry.ToolResult(
            sourceId,
            sequence,
            new ToolExecutionReceipt(
                new ExecutionId("exec_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                ToolExecutionOutcome.Succeeded,
                "ok",
                new PayloadId("payload_11111111111111111111111111111111"),
                sideEffectsPossible: false,
                providerCallId: "provider-call-1"),
            "{\"content\":\"bounded result\"}");
}
