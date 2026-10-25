using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Harness.Core.Tests.Prompting;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData("All checks passed.", false)]
    [InlineData("[BLOCKED] All checks passed.", false)]
    [InlineData("[BLOCKED] All checks passed.", true)]
    public async Task Expected_exit_mismatch_reports_observed_failure_once_without_retrying_final_prose(string modelText, bool earlierFailure)
    {
        var sequence = earlierFailure ? 3 : 1;
        IEnumerable<ModelContextEntry> entries = VerificationCompletionTests.Batch(sequence, "wrapper", "powershell",
            """{"script":"python -c \"import sys;sys.exit(7)\"","purpose":"expected_failure","expected_exit_code":7}""",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(VerificationCompletionTests.Batch(sequence + 2, "native", "run_process",
                """{"executable":"python","arguments":["-c","import sys;sys.exit(7)"],"purpose":"expected_failure","expected_exit_code":7}""",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":7}"""))
            .Concat(VerificationCompletionTests.Batch(sequence + 4, "marker", "powershell",
                """{"script":"Write-Output 'OK'","purpose":"inspection"}""",
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        if (earlierFailure)
            entries = VerificationCompletionTests.Batch(1, "failed-test", "run_process",
                """{"executable":"dotnet","arguments":["test"],"purpose":"verification"}""",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""").Concat(entries);
        var context = new FixedContextSource(entries.ToArray());
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep(modelText), modelText);
        var frames = new List<HarnessTransientFrame>();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));

        var result = await Coordinator(model, new FakeToolGateway(), store, contextSource: context,
                frames: new TransientFrameBuffer(32, frames.Add))
            .RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        var primaryCode = earlierFailure ? "process_exit_nonzero" : "expected_exit_mismatch";
        Assert.Equal(primaryCode, result.ErrorCode);
        var commit = store.Commits.Last();
        Assert.Single(commit.ConversationOutbox);
        var payload = Assert.Single(commit.Payloads, p => p.Kind == HarnessPayloadKind.AssistantText);
        var text = Encoding.UTF8.GetString(payload.Plaintext.Span);
        Assert.Contains("expected 7", text);
        Assert.Contains("observed 1", text);
        Assert.DoesNotContain("All checks passed", text);
        if (earlierFailure) Assert.Contains("Other outstanding checks", text);
        Assert.DoesNotContain(commit.Events, e => e.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Single(model.Requests);
        Assert.True(frames.Last(frame => frame.Kind == TransientFrameKind.AssistantDelta).IsReset);
        Assert.Equal(primaryCode, VerificationCompletionTests.Snapshot(entries).VerificationEvidence.BlockingCode);
    }
}
