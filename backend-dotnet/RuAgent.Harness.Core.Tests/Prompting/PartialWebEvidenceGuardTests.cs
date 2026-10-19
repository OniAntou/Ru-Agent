using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class PartialWebEvidenceGuardTests
{
    [Fact]
    public void Readable_page_can_support_a_reply_when_another_page_is_empty()
    {
        var context = Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Theory]
    [InlineData("write", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "2.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "rule_denied", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.OutcomeUnknown, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Cancelled, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", true, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, false, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, false)]
    public void Missing_evidence_binding_or_other_failures_remain_guarded(string tool, string version,
        ToolExecutionOutcome outcome, string code, bool effects, bool matchingProvider, bool hasSuccess)
    {
        Assert.True(FailedToolCompletionGuard.TryGetFailure(
            Context(tool, version, outcome, code, effects, matchingProvider, hasSuccess), out _));
    }

    private static ModelContextSnapshot Context(string secondTool, string secondVersion, ToolExecutionOutcome secondOutcome,
        string secondCode, bool effects, bool matchingProvider, bool hasSuccess)
    {
        using var args = JsonDocument.Parse("{\"url\":\"https://example.invalid/fixture\"}");
        ModelToolCall[] calls = [new("source_one", "web_fetch", "1.0.0", args.RootElement),
            new("source_two", secondTool, secondVersion, args.RootElement)];
        ToolExecutionReceipt Receipt(int id, ToolExecutionOutcome outcome, string code, bool sideEffects, string provider) =>
            new(new("exec_" + id.ToString("x32")), new("call_" + id.ToString("x32")), outcome, code, null, sideEffects,
                providerCallId: provider);
        ModelContextEntry[] entries = [
            ModelContextEntry.Assistant("sources", 1, new(null, calls, AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("source-one", 2,
                Receipt(1, hasSuccess ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                    hasSuccess ? "ok" : "web_content_empty", false, "source_one"), "{\"text\":\"source evidence\"}"),
            ModelContextEntry.ToolResult("source-two", 3,
                Receipt(2, secondOutcome, secondCode, effects, matchingProvider ? "source_two" : "foreign_call"), "{}"),
        ];
        return new(entries, "[]"u8.ToArray());
    }
}
