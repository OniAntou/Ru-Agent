using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class CompletionChecklistTests
{
    private const string Verification = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";

    [Fact]
    public void Latest_todo_write_supplies_the_open_items_and_advises_once()
    {
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending"), ("b", "completed")));

        Assert.NotNull(context.VerificationEvidence.LatestTodos);
        Assert.Equal(2, context.VerificationEvidence.LatestTodos!.Count);
        Assert.Equal(new[] { "a" }, context.VerificationEvidence.OpenTodoItems);
        Assert.False(context.VerificationEvidence.HasCompletedTodoList);
        Assert.True(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void A_later_fully_completed_list_replaces_the_open_items()
    {
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending"), ("b", "in_progress"))
            .Concat(TodoWrite(3, "todo-2", ("a", "completed"), ("b", "completed"))));

        Assert.Empty(context.VerificationEvidence.OpenTodoItems);
        Assert.True(context.VerificationEvidence.HasCompletedTodoList);
        Assert.False(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void A_failed_todo_write_does_not_replace_the_list()
    {
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending"))
            .Concat(FailedTodoWrite(3, "todo-bad", "todo_multiple_active",
                ("x", "in_progress"), ("y", "in_progress"))));

        Assert.NotNull(context.VerificationEvidence.LatestTodos);
        Assert.Equal(new[] { "a" }, context.VerificationEvidence.OpenTodoItems);
        Assert.True(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void A_later_empty_todo_write_yields_an_empty_list_not_null()
    {
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending"))
            .Concat(TodoWrite(3, "todo-2")));

        Assert.NotNull(context.VerificationEvidence.LatestTodos);
        Assert.Empty(context.VerificationEvidence.LatestTodos!);
        Assert.False(context.VerificationEvidence.HasCompletedTodoList);
        Assert.False(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void Without_a_todo_write_there_is_no_list()
    {
        var context = Snapshot(Edit(1).Concat(Passing(3)));

        Assert.Null(context.VerificationEvidence.LatestTodos);
        Assert.Empty(context.VerificationEvidence.OpenTodoItems);
        Assert.False(context.VerificationEvidence.HasCompletedTodoList);
    }

    [Fact]
    public void Open_items_do_not_advise_while_a_required_check_is_outstanding()
    {
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending")).Concat(Failing(3)));

        Assert.NotNull(context.VerificationEvidence.BlockingCode);
        Assert.False(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void An_existing_open_todos_note_spends_the_single_nudge()
    {
        var note = ModelContextEntry.Text("open-todos-note", 5, ModelContextRole.System,
            AgentRecoveryNotes.BuildOpenTodosNote(new[] { "a" }));
        var context = Snapshot(TodoWrite(1, "todo-1", ("a", "pending")).Append(note));

        Assert.Equal(1, AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.OpenTodos));
        Assert.False(CompletionChecklistGuard.ShouldAdviseOpenTodos(context));
    }

    [Fact]
    public void Source_edit_with_a_passing_check_and_no_todos_asks_for_one_completion_review()
    {
        var context = Snapshot(Edit(1, "src/a.cs").Concat(Passing(3)));

        Assert.True(context.VerificationEvidence.HasSourceEdits);
        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.True(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void A_completed_todo_list_is_the_review_so_no_separate_hold_is_added()
    {
        var context = Snapshot(Edit(1, "src/a.cs").Concat(Passing(3))
            .Concat(TodoWrite(5, "todo-1", ("a", "completed"))));

        Assert.True(context.VerificationEvidence.HasSourceEdits);
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void Edit_awaiting_verification_is_left_to_the_unverified_source_edit_hold()
    {
        var context = Snapshot(Edit(1));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void Edit_that_already_spent_the_source_edit_note_does_not_ask_for_a_review_too()
    {
        var note = ModelContextEntry.Text("source-edit-note", 5, ModelContextRole.System,
            AgentRecoveryNotes.BuildUnverifiedSourceEditNote());
        var context = Snapshot(Edit(1, "src/a.cs").Concat(Passing(3)).Append(note));

        Assert.Equal(1, AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.UnverifiedSourceEdit));
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void Documentation_only_edit_does_not_count_as_a_source_edit()
    {
        var context = Snapshot(Batch(1, "docs", "write_file",
            """{"path":"README.md","content":"notes"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.False(context.VerificationEvidence.HasSourceEdits);
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void Read_only_run_does_not_ask_for_a_completion_review()
    {
        var context = Snapshot(Batch(1, "read", "read_file",
            """{"path":"src/a.cs"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.False(context.VerificationEvidence.HasSourceEdits);
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void An_existing_completion_review_note_spends_the_single_review()
    {
        var note = ModelContextEntry.Text("review-note", 5, ModelContextRole.System,
            AgentRecoveryNotes.BuildCompletionReviewNote());
        var context = Snapshot(Edit(1, "src/a.cs").Concat(Passing(3)).Append(note));

        Assert.Equal(1, AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.CompletionReview));
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void A_blocking_check_suppresses_the_completion_review()
    {
        var context = Snapshot(Edit(1, "src/a.cs").Concat(Failing(3)));

        Assert.True(context.VerificationEvidence.HasSourceEdits);
        Assert.NotNull(context.VerificationEvidence.BlockingCode);
        Assert.False(CompletionChecklistGuard.ShouldAdviseCompletionReview(context));
    }

    [Fact]
    public void Seeded_source_edit_and_todos_survive_projection_and_workspace_freshness()
    {
        var previous = RunVerificationEvidence.Project(Edit(1).Concat(TodoWrite(3, "todo-1", ("a", "pending"))));
        Assert.True(previous.HasSourceEdits);
        Assert.NotNull(previous.LatestTodos);

        var carried = RunVerificationEvidence.Project(Array.Empty<ModelContextEntry>(), previous);
        Assert.True(carried.HasSourceEdits);
        Assert.Equal(new[] { "a" }, carried.OpenTodoItems);

        var fresh = carried.ApplyWorkspaceFreshness(new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>());
        Assert.True(fresh.HasSourceEdits);
        Assert.Equal(new[] { "a" }, fresh.OpenTodoItems);
        Assert.Equal(carried.LatestTodos!.Count, fresh.LatestTodos!.Count);
    }

    [Fact]
    public void Open_todos_note_lists_eight_bounded_items_and_the_remainder()
    {
        var items = new List<string> { new string('x', 200), "first line\nsecond line" };
        for (var index = 2; index < 10; index++) items.Add("item " + index);

        var note = AgentRecoveryNotes.BuildOpenTodosNote(items);

        Assert.StartsWith("[HARNESS RECOVERY] open_todos - the final reply was held once: your task list still has open items: ",
            note, StringComparison.Ordinal);
        Assert.Equal(8, Regex.Matches(note, "\"[^\"]*\"").Count);
        Assert.Contains("\"" + new string('x', 120) + "...\"", note, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 121), note, StringComparison.Ordinal);
        Assert.Contains("\"first line second line\"", note, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", note, StringComparison.Ordinal);
        Assert.Contains("item 7\"", note, StringComparison.Ordinal);
        Assert.DoesNotContain("item 8", note, StringComparison.Ordinal);
        Assert.Contains("(and 2 more). Finish and check each one, then mark it completed with todo_write; "
            + "remove an item only if it is no longer needed and say why in the final reply.", note, StringComparison.Ordinal);
        Assert.EndsWith("Finish and check each one, then mark it completed with todo_write; "
            + "remove an item only if it is no longer needed and say why in the final reply.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_todos_truncation_never_splits_a_surrogate_pair()
    {
        var items = new[] { new string('x', 119) + "\U0001F600" };

        var note = AgentRecoveryNotes.BuildOpenTodosNote(items);

        Assert.Contains("\"" + new string('x', 119) + "...\"", note, StringComparison.Ordinal);
        Assert.DoesNotContain('\uD83D', note);
    }

    [Fact]
    public void Completion_review_note_starts_with_its_one_time_recovery_marker()
    {
        var note = AgentRecoveryNotes.BuildCompletionReviewNote();

        Assert.StartsWith("[HARNESS RECOVERY] completion_review - ", note, StringComparison.Ordinal);
        Assert.Contains("purpose=verification", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Core_contract_asks_for_one_todo_per_explicit_requirement_and_a_final_requirement_check()
    {
        var contract = RuAgentPromptBlocks.CoreContractBlock();

        Assert.Contains("When a coding request states several requirements, record each explicit requirement "
            + "(behavior, edge and error cases, ordering, return values, allowed files, cleanup) as its own todo item "
            + "before editing; mark an item completed only after a check that exercises it passes.", contract, StringComparison.Ordinal);
        Assert.Contains("Before the final reply, re-read the request and your changes and confirm each requirement is met; "
            + "then report the result and any remaining limits.", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Once the requested change and relevant verification are complete", contract, StringComparison.Ordinal);
    }

    private static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) =>
        VerificationCompletionTests.Snapshot(entries);

    private static ModelContextEntry[] Batch(long sequence, string id, string tool, string args,
        ToolExecutionOutcome outcome, string code, string content) =>
        VerificationCompletionTests.Batch(sequence, id, tool, args, outcome, code, content);

    private static ModelContextEntry[] TodoWrite(long sequence, string id, params (string Content, string Status)[] rows)
    {
        var json = TodoPayload(rows);
        return Batch(sequence, id, "todo_write", json, ToolExecutionOutcome.Succeeded, "ok", json);
    }

    /// <summary>
    /// The rejected call carries a parseable todos array on purpose: the test must prove the
    /// outcome gate, not a parse failure, is what keeps the earlier list.
    /// </summary>
    private static ModelContextEntry[] FailedTodoWrite(long sequence, string id, string code,
        params (string Content, string Status)[] rows) =>
        Batch(sequence, id, "todo_write", TodoPayload(rows), ToolExecutionOutcome.Failed, code, TodoPayload(rows));

    private static string TodoPayload(IEnumerable<(string Content, string Status)> rows) =>
        new JsonObject
        {
            ["todos"] = new JsonArray(rows.Select(row => (JsonNode)new JsonObject
            {
                ["content"] = row.Content,
                ["status"] = row.Status,
            }).ToArray()),
        }.ToJsonString();

    private static ModelContextEntry[] Edit(long sequence, string path = "src/a.cs") =>
        Batch(sequence, "edit-" + sequence, "edit_file", $$"""{"path":"{{path}}","edits":[]}""",
            ToolExecutionOutcome.Succeeded, "ok", "{}");

    private static ModelContextEntry[] Passing(long sequence) =>
        Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");

    private static ModelContextEntry[] Failing(long sequence) =>
        Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");
}
