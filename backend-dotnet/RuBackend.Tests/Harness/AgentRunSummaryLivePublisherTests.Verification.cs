using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentRunSummaryLivePublisherTests
{
    [Theory]
    [InlineData(true, false, "verified")]
    [InlineData(false, false, "unverified")]
    [InlineData(true, true, "unverified")]
    public async Task Terminal_summary_uses_original_receipt_checks_even_without_live_activity(
        bool passed, bool editAfterCheck, string expected)
    {
        var entries = SummaryBatch(1, "edit_file", """{"path":"summary.txt","edits":[{"old_string":"before","new_string":"after"}]}""", true, "{}").ToList();
        entries.AddRange(SummaryBatch(3, "powershell",
            """{"script":"dotnet test","purpose":"verification"}""",
            passed, passed ? """{"exit_code":0}""" : """{"exit_code":1}"""));
        if (editAfterCheck) entries.AddRange(SummaryBatch(5, "edit_file", """{"path":"summary.txt","edits":[{"old_string":"checked","new_string":"later"}]}""", true, "{}"));
        var source = new SummaryContextSource(entries);
        var publisher = new AgentRunSummaryLivePublisher(new HarnessLiveEventHub(), contextSource: source);
        var run = MakeRun(HarnessRunState.Completed);
        Assert.True(await publisher.TryReserveSummaryAsync(run, default));
        Assert.Equal(expected, publisher.BuildSummary(run).VerificationState);
        Assert.False(await publisher.TryReserveSummaryAsync(run, default));
        Assert.Equal(1, source.Loads);
    }

    [Fact]
    public async Task Restarted_publisher_does_not_certify_an_edit_without_checks()
    {
        var publisher = new AgentRunSummaryLivePublisher(new HarnessLiveEventHub(), contextSource:
            new SummaryContextSource(SummaryBatch(1, "edit_file", """{"path":"summary.txt","edits":[{"old_string":"before","new_string":"after"}]}""", true, "{}")));
        var run = MakeRun(HarnessRunState.Completed);
        await publisher.TryReserveSummaryAsync(run, default);
        Assert.Equal("unverified", publisher.BuildSummary(run).VerificationState);
    }

    private sealed class SummaryContextSource(IReadOnlyList<ModelContextEntry> entries) : IModelContextSource
    {
        public int Loads { get; private set; }
        public Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Loads++;
            return Task.FromResult(new ModelContextSourceSnapshot([], entries));
        }
    }

    private static ModelContextEntry[] SummaryBatch(long sequence, string tool, string args, bool success, string output)
    {
        using var json = JsonDocument.Parse(args);
        var id = "check-" + sequence;
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + sequence.ToString("x32")),
            new ToolCallId("call_" + sequence.ToString("x32")),
            success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
            success ? "ok" : "process_exit_nonzero", null, tool == "edit_file", providerCallId: id);
        return [ModelContextEntry.Assistant("proposal-" + id, sequence,
            new AssistantStep(null, [new ModelToolCall(id, tool, "1.0.0", json.RootElement)], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("result-" + id, sequence + 1, receipt, output)];
    }
}
