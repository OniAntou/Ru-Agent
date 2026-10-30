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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_exit_mismatch_is_held_three_times_then_completes_unverified(bool earlierFailure)
    {
        var entries = MismatchEntries(earlierFailure).ToList();
        var source = new FixedContextSource(entries.ToArray());
        var model = new FakeModelStepRunner();
        for (var attempt = 0; attempt <= AgentRecoveryNotes.MaximumUnverifiedNotes; attempt++)
            model.Enqueue(FinalStep("All checks passed."), "All checks passed.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);
        var primaryCode = earlierFailure ? "process_exit_nonzero" : "expected_exit_mismatch";
        var expectedNote = AgentRecoveryNotes.BuildUnverifiedNote(primaryCode, verificationBound: true);

        for (var hold = 0; hold < AgentRecoveryNotes.MaximumUnverifiedNotes; hold++)
        {
            var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

            Assert.Equal(HarnessRunState.Running, held.Run.State);
            Assert.Null(held.ErrorCode);
            var commit = store.Commits.Last();
            Assert.DoesNotContain(commit.Events, e => e.Kind == HarnessEventKind.RunFailed);
            var notePayload = Assert.Single(commit.Payloads, p => p.Kind == HarnessPayloadKind.ModelContextNote);
            Assert.Equal(expectedNote, Encoding.UTF8.GetString(notePayload.Plaintext.Span));
            AppendNote(source, commit.Payloads, sequence: 100 + hold);
        }

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(final.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        var finalCommit = store.Commits.Last();
        Assert.DoesNotContain(finalCommit.Events, e => e.Kind == HarnessEventKind.RunFailed);
        Assert.DoesNotContain(finalCommit.Payloads, p => p.Kind == HarnessPayloadKind.ModelContextNote);
        var reply = Assert.Single(finalCommit.Payloads, p => p.Kind == HarnessPayloadKind.AssistantText);
        Assert.Equal("All checks passed.", Encoding.UTF8.GetString(reply.Plaintext.Span));

        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(), HistoryWaitKind.FinalBeforeCompletion, CancellationToken.None);

        Assert.Equal(HarnessRunState.Completed, completed.State);
        Assert.Null(completed.TerminalErrorCode);
        Assert.Contains(entries, e => e.ToolReceipt?.Outcome == ToolExecutionOutcome.Failed);
        var evidence = VerificationCompletionTests.Snapshot(entries).VerificationEvidence;
        Assert.Equal(primaryCode, evidence.BlockingCode);
        var summary = AgentRunSummary.FromTerminalRun(completed, "Workspace", verificationEvidence: evidence);
        Assert.Equal("succeeded", summary.Status);
        // A declared expected_failure check that was executed and mismatched is an unverified outcome
        // whether or not an earlier check failed (ADR 0039 item 7).
        Assert.Equal("unverified", summary.VerificationState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_reply_after_an_expected_exit_mismatch_hold_still_fails_with_the_check_code(bool earlierFailure)
    {
        var entries = MismatchEntries(earlierFailure).ToList();
        var source = new FixedContextSource(entries.ToArray());
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("All checks passed."), "All checks passed.");
        model.Enqueue(FinalStep("[BLOCKED] The negative test still returns the wrong exit code."),
            "[BLOCKED] The negative test still returns the wrong exit code.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);
        var primaryCode = earlierFailure ? "process_exit_nonzero" : "expected_exit_mismatch";

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 100);

        var blocked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, blocked.Run.State);
        Assert.Equal(primaryCode, blocked.ErrorCode);
        Assert.Equal(primaryCode, blocked.Run.TerminalErrorCode);
        var commit = store.Commits.Last();
        Assert.Contains(commit.Events, e => e.Kind == HarnessEventKind.ModelStepCompleted
            && e.SafeCode == "blocked_report");
        Assert.Contains(commit.Events, e => e.Kind == HarnessEventKind.RunFailed && e.SafeCode == primaryCode);
        var reply = Assert.Single(commit.Payloads, p => p.Kind == HarnessPayloadKind.AssistantText);
        Assert.Equal("The negative test still returns the wrong exit code.",
            Encoding.UTF8.GetString(reply.Plaintext.Span));
    }

    private static IEnumerable<ModelContextEntry> MismatchEntries(bool earlierFailure)
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
        return entries;
    }
}
