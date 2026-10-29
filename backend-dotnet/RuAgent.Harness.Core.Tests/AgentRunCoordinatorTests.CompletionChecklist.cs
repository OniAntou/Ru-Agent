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
    private const string OpenTodoList = """{"todos":[{"content":"a","status":"pending"}]}""";
    private const string CompletedTodoList = """{"todos":[{"content":"a","status":"completed"}]}""";
    private const string EditFileCall = """{"path":"src/x.cs","edits":[]}""";

    [Fact]
    public async Task Open_todo_items_hold_the_final_reply_once_then_a_completed_list_finishes_without_a_review()
    {
        var source = new FixedContextSource();
        AppendEntries(source, TodoWriteEntries(1, "todo-1", OpenTodoList));
        AppendEntries(source, SourceEditEntries(3));
        AppendEntries(source, PassingCheckEntries(5));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X and the tests pass."));
        model.Enqueue(FinalStep("Added X; the checklist is complete."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(held.ErrorCode);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        var commit = store.Commits.Last();
        var note = Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Equal("open_todos", note.SafeCode);
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "open_todos_advised");
        Assert.Empty(store.Commits.SelectMany(item => item.ConversationOutbox));
        AppendNote(source, commit.Payloads, sequence: 7);
        AppendEntries(source, TodoWriteEntries(8, "todo-8", CompletedTodoList));

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.DoesNotContain(
            store.Commits.SelectMany(item => item.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded && item.SafeCode == "completion_review");
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Source_edit_with_a_passing_check_holds_one_completion_review_then_completes()
    {
        var source = new FixedContextSource();
        AppendEntries(source, SourceEditEntries(1));
        AppendEntries(source, PassingCheckEntries(3));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X and verified it."));
        model.Enqueue(FinalStep("Added X; every requirement is checked and verified."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(held.ErrorCode);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        var commit = store.Commits.Last();
        var note = Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Equal("completion_review", note.SafeCode);
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "completion_review_advised");
        Assert.Empty(store.Commits.SelectMany(item => item.ConversationOutbox));
        AppendNote(source, commit.Payloads, sequence: 5);

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
    public async Task Unverified_source_edit_nudge_then_passing_check_never_adds_a_completion_review()
    {
        var source = new FixedContextSource();
        AppendEntries(source, SourceEditEntries(1));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("Added X and the tests pass."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 3);
        AppendEntries(source, PassingCheckEntries(4));

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        var notes = store.Commits
            .SelectMany(item => item.Events)
            .Where(item => item.Kind == HarnessEventKind.RunNoteAdded)
            .ToArray();
        var onlyNote = Assert.Single(notes);
        Assert.Equal("unverified_source_edit", onlyNote.SafeCode);
        Assert.DoesNotContain(notes, item => item.SafeCode == "completion_review");
        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Read_only_run_completes_on_the_first_final_reply_with_no_checklist_note()
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
    public async Task Blocked_reply_after_the_completion_review_hold_still_fails_the_run()
    {
        var source = new FixedContextSource();
        AppendEntries(source, SourceEditEntries(1));
        AppendEntries(source, PassingCheckEntries(3));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X."));
        model.Enqueue(FinalStep("[BLOCKED] the test runner is unavailable in this sandbox"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var held = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, held.Run.State);
        AppendNote(source, store.Commits.Last().Payloads, sequence: 5);

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

    [Fact]
    public async Task Completed_checklist_is_the_review_so_the_final_reply_completes_at_once()
    {
        var source = new FixedContextSource();
        AppendEntries(source, SourceEditEntries(1));
        AppendEntries(source, PassingCheckEntries(3));
        AppendEntries(source, TodoWriteEntries(5, "todo-5", CompletedTodoList));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Added X; every checklist item is done and verified."));
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

    private static ModelContextEntry[] TodoWriteEntries(long sequence, string id, string list) =>
        VerificationCompletionTests.Batch(sequence, id, "todo_write", list,
            ToolExecutionOutcome.Succeeded, "ok", list);

    private static ModelContextEntry[] SourceEditEntries(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "edit-" + sequence, "edit_file", EditFileCall,
            ToolExecutionOutcome.Succeeded, "ok", "{}");

    private static ModelContextEntry[] PassingCheckEntries(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "check-" + sequence, "bash", PassingCheck,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");
}
