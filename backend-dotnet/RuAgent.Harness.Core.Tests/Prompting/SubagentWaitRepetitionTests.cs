using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class SubagentWaitRepetitionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Successful_wait_timeouts_allow_waiting_again_without_repair_notes(int timeouts)
    {
        var (context, next) = Context(timeouts);
        Assert.Equal(RepetitionGuardVerdict.NotRepeated,
            ConsecutiveToolCallGuard.Inspect(context, [next], out _));
    }

    [Theory]
    [InlineData("agent_wait", "1.0.0", ToolExecutionOutcome.Failed, "subagent_wait_timeout", false, true)]
    [InlineData("agent_wait", "1.0.0", ToolExecutionOutcome.Succeeded, "subagent_wait_completed", false, true)]
    [InlineData("agent_wait", "1.0.0", ToolExecutionOutcome.Succeeded, "subagent_wait_timeout", true, true)]
    [InlineData("web_search", "1.0.0", ToolExecutionOutcome.Succeeded, "subagent_wait_timeout", false, true)]
    [InlineData("agent_wait", "2.0.0", ToolExecutionOutcome.Succeeded, "subagent_wait_timeout", false, true)]
    [InlineData("agent_wait", "1.0.0", ToolExecutionOutcome.Succeeded, "subagent_wait_timeout", false, false)]
    public void Other_receipts_and_unbound_timeouts_remain_guarded(string tool, string version,
        ToolExecutionOutcome outcome, string code, bool effects, bool matchingProvider)
    {
        var (context, next) = Context(1, tool, version, outcome, code, effects, matchingProvider);
        Assert.Equal(RepetitionGuardVerdict.Advise,
            ConsecutiveToolCallGuard.Inspect(context, [next], out _));
    }

    private static (ModelContextSnapshot Context, ModelToolCall Next) Context(int count,
        string tool = "agent_wait", string version = "1.0.0", ToolExecutionOutcome outcome = ToolExecutionOutcome.Succeeded,
        string code = "subagent_wait_timeout", bool effects = false, bool matchingProvider = true)
    {
        using var arguments = JsonDocument.Parse("{\"agent_ids\":[\"agent_one\"],\"timeout_ms\":30000}");
        var entries = new List<ModelContextEntry>();
        for (var index = 1; index <= count; index++)
        {
            var providerCall = "provider_wait_" + index;
            var call = new ModelToolCall(providerCall, tool, version, arguments.RootElement);
            entries.Add(ModelContextEntry.Assistant("assistant-" + index, entries.Count + 1,
                new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)));
            var receipt = new ToolExecutionReceipt(new("exec_" + index.ToString("x32")),
                new("call_" + index.ToString("x32")), outcome, code, null, effects,
                providerCallId: matchingProvider ? providerCall : "foreign_provider_call");
            entries.Add(ModelContextEntry.ToolResult("result-" + index, entries.Count + 1, receipt, "{}"));
        }
        return (new(entries, "[]"u8.ToArray()), new("provider_next", tool, version, arguments.RootElement));
    }
}
