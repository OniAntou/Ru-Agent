using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class ComputerRepetitionTests
{
    [Theory]
    [InlineData("{\"action\":\"screenshot\",\"monitor\":0}")]
    [InlineData("{\"action\":\"key\",\"keys\":\"down\"}")]
    public void Repeating_a_successful_computer_call_is_progress(string arguments)
    {
        var (context, next) = Context("computer", arguments, ToolExecutionOutcome.Succeeded, "ok");
        Assert.Equal(RepetitionGuardVerdict.NotRepeated, ConsecutiveToolCallGuard.Inspect(context, [next], out _));
    }

    [Theory]
    [InlineData("computer", ToolExecutionOutcome.Failed, "computer_app_not_granted")]
    [InlineData("web_search", ToolExecutionOutcome.Succeeded, "ok")]
    public void Failed_computer_calls_and_other_tools_stay_guarded(string tool, ToolExecutionOutcome outcome, string code)
    {
        var (context, next) = Context(tool, "{\"action\":\"screenshot\"}", outcome, code);
        Assert.Equal(RepetitionGuardVerdict.Advise, ConsecutiveToolCallGuard.Inspect(context, [next], out _));
    }

    private static (ModelContextSnapshot Context, ModelToolCall Next) Context(
        string tool, string json, ToolExecutionOutcome outcome, string code)
    {
        using var arguments = JsonDocument.Parse(json);
        var call = new ModelToolCall("provider_one", tool, "1.0.0", arguments.RootElement.Clone());
        var receipt = new ToolExecutionReceipt(new("exec_" + 1.ToString("x32")), new("call_" + 1.ToString("x32")),
            outcome, code, null, false, providerCallId: "provider_one");
        ModelContextEntry[] entries =
        [
            ModelContextEntry.Assistant("assistant-1", 1, new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("result-1", 2, receipt, "{}"),
        ];
        return (new(entries, "[]"u8.ToArray()), new("provider_two", tool, "1.0.0", arguments.RootElement.Clone()));
    }
}
