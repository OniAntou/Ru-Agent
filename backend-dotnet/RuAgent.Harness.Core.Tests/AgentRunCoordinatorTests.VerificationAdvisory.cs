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
    private const string DeclaredCheckArgs = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";

    [Fact]
    public async Task Failed_declared_check_is_held_three_times_then_completes_unverified_and_keeps_its_receipt()
    {
        var failed = VerificationCompletionTests.Batch(1, "test", "bash", DeclaredCheckArgs,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""").ToList();
        var source = new FixedContextSource(failed.ToArray());
        var model = new FakeModelStepRunner();
        for (var attempt = 0; attempt <= AgentRecoveryNotes.MaximumUnverifiedNotes; attempt++)
            model.Enqueue(FinalStep("All tests pass."), "All tests pass.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        for (var hold = 0; hold < AgentRecoveryNotes.MaximumUnverifiedNotes; hold++)
        {
            var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

            Assert.Equal(HarnessRunState.Running, held.Run.State);
            var commit = store.Commits.Last();
            Assert.Contains(commit.Events, e => e.Kind == HarnessEventKind.ModelStepCompleted
                && e.SafeCode == "unverified_after_tool_failure_advised");
            var notePayload = Assert.Single(commit.Payloads, p => p.Kind == HarnessPayloadKind.ModelContextNote);
            Assert.Equal(AgentRecoveryNotes.BuildUnverifiedNote("process_exit_nonzero", verificationBound: true),
                Encoding.UTF8.GetString(notePayload.Plaintext.Span));
            AppendNote(source, commit.Payloads, sequence: 10 + hold);
        }

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(), HistoryWaitKind.FinalBeforeCompletion, CancellationToken.None);

        Assert.Equal(HarnessRunState.Completed, completed.State);
        Assert.Null(completed.TerminalErrorCode);
        Assert.Contains(failed, e => e.ToolReceipt?.Outcome == ToolExecutionOutcome.Failed);
        var evidence = VerificationCompletionTests.Snapshot(failed).VerificationEvidence;
        Assert.Equal("process_exit_nonzero", evidence.BlockingCode);
        Assert.Equal("unverified",
            AgentRunSummary.FromTerminalRun(completed, "Workspace", verificationEvidence: evidence).VerificationState);
    }

    [Fact]
    public async Task Passing_rerun_of_a_held_check_completes_and_its_summary_is_verified()
    {
        var failed = VerificationCompletionTests.Batch(1, "test", "bash", DeclaredCheckArgs,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""").ToList();
        var source = new FixedContextSource(failed.ToArray());
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("All tests pass."), "All tests pass.");
        model.Enqueue(FinalStep("All tests pass now."), "All tests pass now.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 3);

        // The model reruns the same declared check, and this time it passes.
        var rerun = VerificationCompletionTests.Batch(4, "test-rerun", "bash", DeclaredCheckArgs,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""").ToList();
        foreach (var entry in rerun)
            source.Add(entry);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.DoesNotContain(store.Commits.Last().Payloads, p => p.Kind == HarnessPayloadKind.ModelContextNote);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(), HistoryWaitKind.FinalBeforeCompletion, CancellationToken.None);

        Assert.Equal(HarnessRunState.Completed, completed.State);
        var evidence = VerificationCompletionTests.Snapshot(failed.Concat(rerun)).VerificationEvidence;
        Assert.Null(evidence.BlockingCode);
        Assert.Equal("verified",
            AgentRunSummary.FromTerminalRun(completed, "Workspace", verificationEvidence: evidence).VerificationState);
    }

    [Fact]
    public async Task Passed_check_then_source_edit_holds_only_the_source_edit_nudge_and_completes_unverified()
    {
        // A passed check followed by a structured edit is not a failed tool result. The only hold is the
        // one-time source-edit nudge, and the run completes with its label unverified (ADR 0039 items 1, 7).
        var entries = VerificationCompletionTests.Batch(1, "check", "bash", DeclaredCheckArgs,
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""")
            .Concat(VerificationCompletionTests.Batch(3, "edit", "write_file",
                """{"path":"src/x.cs","content":"class X {}"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"))
            .ToList();
        var source = new FixedContextSource(entries.ToArray());
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("Added X."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, held.Run.State);
        var commit = store.Commits.Last();
        Assert.Equal("unverified_source_edit",
            Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded).SafeCode);
        Assert.DoesNotContain(commit.Events, item => item.SafeCode == "unverified_after_tool_failure_advised");
        AppendNote(source, commit.Payloads, sequence: 5);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(), HistoryWaitKind.FinalBeforeCompletion, CancellationToken.None);

        Assert.Equal(HarnessRunState.Completed, completed.State);
        Assert.Equal("unverified", AgentRunSummary.FromTerminalRun(completed, "Workspace",
            verificationEvidence: VerificationCompletionTests.Snapshot(entries).VerificationEvidence).VerificationState);
    }
}
