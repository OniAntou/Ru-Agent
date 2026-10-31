using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Harness.Core.Tests.Prompting;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    private const string PassingCheck = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";
    private const string SourceEdit = """{"path":"src/x.cs","content":"class X {}"}""";

    [Fact]
    public async Task Unverified_source_edit_holds_the_first_final_reply_once_with_the_note()
    {
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "edit", "write_file",
            SourceEdit, ToolExecutionOutcome.Succeeded, "ok", "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("Added X and finished."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(held.ErrorCode);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        var commit = store.Commits.Last();
        var note = Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Equal("unverified_source_edit", note.SafeCode);
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "unverified_source_edit_advised");
        Assert.Empty(store.Commits.SelectMany(item => item.ConversationOutbox));

        AppendNote(source, commit.Payloads, sequence: 3);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
        Assert.Single(
            store.Commits.SelectMany(item => item.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
    }

    [Fact]
    public async Task Unverified_source_edit_nudge_then_passing_check_completes_without_a_second_note()
    {
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "edit", "write_file",
            SourceEdit, ToolExecutionOutcome.Succeeded, "ok", "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("Added X and the tests pass."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 3);
        AppendEntries(source, VerificationCompletionTests.Batch(4, "check", "bash",
            PassingCheck, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.Single(
            store.Commits.SelectMany(item => item.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Unverified_source_edit_read_only_run_completes_on_the_first_final_reply_without_a_note()
    {
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "read", "read_file",
            """{"path":"src/x.cs"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("The file defines X."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.DoesNotContain(
            store.Commits.SelectMany(item => item.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Unverified_source_edit_passing_check_holds_one_completion_review_before_completing()
    {
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "edit", "write_file",
            SourceEdit, ToolExecutionOutcome.Succeeded, "ok", "{}"));
        AppendEntries(source, VerificationCompletionTests.Batch(3, "check", "bash",
            PassingCheck, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X and verified it."));
        model.Enqueue(FinalStep("Added X and verified it; every requirement is checked."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(held.ErrorCode);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        var note = Assert.Single(
            store.Commits.Last().Events,
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Equal("completion_review", note.SafeCode);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 5);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.Single(
            store.Commits.SelectMany(item => item.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Unverified_source_edit_blocked_reply_after_the_nudge_still_fails_the_run()
    {
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "edit", "write_file",
            SourceEdit, ToolExecutionOutcome.Succeeded, "ok", "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("[BLOCKED] the test runner is unavailable in this sandbox"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 3);

        var blocked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, blocked.Run.State);
        Assert.Equal("model_reported_blocker", blocked.ErrorCode);
        Assert.Equal("model_reported_blocker", blocked.Run.TerminalErrorCode);
        var commit = store.Commits.Last();
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.RunFailed && item.SafeCode == "model_reported_blocker");
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted && item.SafeCode == "blocked_report");
    }

    private static void AppendNote(
        FixedContextSource source,
        IEnumerable<HarnessPayloadIntent> payloads,
        long sequence)
    {
        var notePayload = Assert.Single(
            payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
        source.Add(ModelContextEntry.Text(
            $"note-{sequence}",
            sequence,
            ModelContextRole.System,
            Encoding.UTF8.GetString(notePayload.Plaintext.Span)));
    }

    private static void AppendEntries(FixedContextSource source, IEnumerable<ModelContextEntry> entries)
    {
        foreach (var entry in entries)
        {
            source.Add(entry);
        }
    }
}
