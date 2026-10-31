using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Completion;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Completion;

public sealed class CompletionPolicyTests
{
    private const string Verification = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";

    // R1: unusable finish reason, before the freshness refresh.

    [Theory]
    [InlineData(AssistantFinishReason.ContentFilter, "model_content_filtered")]
    [InlineData(AssistantFinishReason.Error, "model_reply_error")]
    public void R1_filtered_or_errored_reply_fails_at_once_and_retracts_its_text(
        AssistantFinishReason finish, string code)
    {
        var decision = Before(Step("partial", finish), Empty());

        AssertDecision(decision, StepDecisionKind.Fail, "R1.unusable_finish");
        Assert.Equal(code, decision.ErrorCode);
        Assert.True(decision.RetractStreamedText);
        Assert.Null(decision.NoteKind);
    }

    [Fact]
    public void R1_truncated_reply_is_held_with_the_truncation_note_and_retracted()
    {
        var decision = Before(Step("partial", AssistantFinishReason.Length), Empty());

        AssertHold(decision, "R1.unusable_finish", AgentRecoveryKind.OutputTruncated,
            AgentRecoveryNotes.BuildTruncatedNote());
        Assert.True(decision.RetractStreamedText);
        Assert.False(decision.RetainAssistantContext);
        Assert.Equal(0, decision.PriorNoteCount);
    }

    [Fact]
    public void R1_truncation_fails_with_its_code_once_three_consecutive_notes_are_spent()
    {
        var context = Snapshot(Notes(AgentRecoveryNotes.BuildTruncatedNote(), 3));

        var decision = Before(Step("partial", AssistantFinishReason.Length), context);

        AssertDecision(decision, StepDecisionKind.Fail, "R1.unusable_finish");
        Assert.Equal("model_output_truncated", decision.ErrorCode);
        Assert.True(decision.RetractStreamedText);
    }

    [Fact]
    public void R1_truncated_step_with_tool_calls_is_unusable_and_never_dispatched()
    {
        var decision = Before(ToolStep(AssistantFinishReason.Length), Empty());

        AssertHold(decision, "R1.unusable_finish", AgentRecoveryKind.OutputTruncated,
            AgentRecoveryNotes.BuildTruncatedNote());
    }

    // R2: tool calls keep the coordinator's dispatch path.

    [Fact]
    public void R2_tool_calls_proceed_to_dispatch_without_retraction()
    {
        var decision = Before(ToolStep(), Empty());

        AssertDecision(decision, StepDecisionKind.Proceed, "R2.tool_calls");
        Assert.False(decision.RetractStreamedText);
        Assert.Null(decision.NoteKind);
    }

    [Fact]
    public void R2_tool_calls_win_over_a_commentary_phase()
    {
        var step = new AssistantStep("Inspecting.", [ReadCall()], AssistantFinishReason.ToolCalls, 1,
            responsesMessages: Phase("commentary", "Inspecting."));

        var decision = Before(step, Empty());

        AssertDecision(decision, StepDecisionKind.Proceed, "R2.tool_calls");
    }

    // R3: commentary-only progress.

    [Fact]
    public void R3_commentary_only_step_is_held_in_model_history_without_retracting_its_progress()
    {
        var decision = Before(CommentaryStep("Inspecting the repo."), Empty());

        AssertHold(decision, "R3.commentary_only", AgentRecoveryKind.CommentaryOnly,
            AgentRecoveryNotes.BuildCommentaryNote());
        Assert.False(decision.RetractStreamedText);
        Assert.True(decision.RetainAssistantContext);
    }

    [Fact]
    public void R3_commentary_fails_with_its_code_once_its_notes_are_spent_and_keeps_its_text()
    {
        var context = Snapshot(Notes(AgentRecoveryNotes.BuildCommentaryNote(), 3));

        var decision = Before(CommentaryStep("Inspecting."), context);

        AssertDecision(decision, StepDecisionKind.Fail, "R3.commentary_only");
        Assert.Equal("model_commentary_only", decision.ErrorCode);
        Assert.False(decision.RetractStreamedText);
    }

    [Fact]
    public void R3_commentary_wins_over_a_blocked_looking_reply()
    {
        var decision = Before(CommentaryStep("[BLOCKED] cannot continue"), Empty());

        AssertDecision(decision, StepDecisionKind.Hold, "R3.commentary_only");
    }

    [Fact]
    public void Plain_final_prose_has_no_pre_refresh_decision()
    {
        Assert.Null(CompletionPolicy.DecideBeforeRefresh(Step("Done."), Empty()));
    }

    // R4: failure, pending or stale evidence, and explicit blockers, after the refresh.

    [Fact]
    public void R4a_blocked_reply_with_an_exit_mismatch_keeps_its_text_and_reports_the_check_code()
    {
        var decision = After(Step("[BLOCKED] The check failed."), Snapshot(Mismatch(1)));

        AssertDecision(decision, StepDecisionKind.BlockedReport, "R4a.blocked_reply");
        Assert.Equal("expected_exit_mismatch", decision.ErrorCode);
        Assert.Equal("The check failed.", decision.ReportText);
        Assert.False(decision.RetractStreamedText);
    }

    [Fact]
    public void R4a_blocked_reply_without_a_failure_is_an_explicit_blocker_and_keeps_its_streamed_text()
    {
        var decision = After(Step("[BLOCKED] Need the API key."), Empty());

        AssertDecision(decision, StepDecisionKind.BlockedReport, "R4a.blocked_reply");
        Assert.Equal("Need the API key.", decision.ReportText);
        Assert.Equal("model_reported_blocker", decision.ErrorCode);
        Assert.False(decision.RetractStreamedText);
    }

    [Fact]
    public void R4a_blocked_reply_after_a_failed_receipt_reports_the_receipt_code()
    {
        var decision = After(Step("[BLOCKED] The edit could not apply."), Snapshot(FailedRead(1)));

        AssertDecision(decision, StepDecisionKind.BlockedReport, "R4a.blocked_reply");
        Assert.Equal("edit_no_match", decision.ErrorCode);
    }

    [Fact]
    public void R4a_blocked_reply_after_an_outstanding_check_hold_still_fails_with_the_check_code()
    {
        var context = Snapshot(Failing(3).Concat(Notes(VerificationNote("process_exit_nonzero"), 1, 50)));

        var decision = After(Step("[BLOCKED] The check still fails."), context);

        AssertDecision(decision, StepDecisionKind.BlockedReport, "R4a.blocked_reply");
        Assert.Equal("process_exit_nonzero", decision.ErrorCode);
        Assert.Equal("The check still fails.", decision.ReportText);
        Assert.False(decision.RetractStreamedText);
    }

    [Fact]
    public void R4c_prose_after_a_failed_receipt_is_held_once_with_the_unverified_note()
    {
        var decision = After(Step("All done."), Snapshot(FailedRead(1)));

        AssertHold(decision, "R4c.unverified_advice", AgentRecoveryKind.UnverifiedAfterToolFailure,
            AgentRecoveryNotes.BuildUnverifiedNote("edit_no_match"));
        Assert.True(decision.RetractStreamedText);
        Assert.Equal(0, decision.PriorNoteCount);
    }

    [Fact]
    public void R4c_expected_exit_mismatch_is_held_with_its_code_and_not_reported_at_once()
    {
        var decision = After(Step("All checks passed."), Snapshot(Mismatch(1)));

        AssertHold(decision, "R4c.unverified_advice", AgentRecoveryKind.UnverifiedAfterToolFailure,
            VerificationNote("expected_exit_mismatch"));
        Assert.True(decision.RetractStreamedText);
        Assert.Equal(0, decision.PriorNoteCount);
    }

    [Fact]
    public void R4c_outstanding_check_hold_counts_the_notes_already_issued_against_the_bound()
    {
        var context = Snapshot(Failing(3).Concat(Notes(VerificationNote("process_exit_nonzero"), 2, 50)));

        var decision = After(Step("All checks passed."), context);

        AssertHold(decision, "R4c.unverified_advice", AgentRecoveryKind.UnverifiedAfterToolFailure,
            VerificationNote("process_exit_nonzero"));
        Assert.Equal(2, decision.PriorNoteCount);
    }

    [Fact]
    public void R4d_outstanding_check_completes_without_retraction_once_its_three_holds_are_spent()
    {
        var context = Snapshot(Failing(3).Concat(Notes(VerificationNote("process_exit_nonzero"), 3, 50)));

        var decision = After(Step("All checks passed."), context);

        AssertDecision(decision, StepDecisionKind.Complete, "R4d.verification_advisory_complete");
        Assert.False(decision.RetractStreamedText);
        Assert.Null(decision.ErrorCode);
        Assert.Null(decision.NoteKind);
    }

    [Fact]
    public void R4d_expected_exit_mismatch_completes_once_its_three_holds_are_spent()
    {
        var context = Snapshot(Mismatch(1).Concat(Notes(VerificationNote("expected_exit_mismatch"), 3, 50)));

        var decision = After(Step("All checks passed."), context);

        AssertDecision(decision, StepDecisionKind.Complete, "R4d.verification_advisory_complete");
        Assert.False(decision.RetractStreamedText);
    }

    [Fact]
    public void R4d_blank_reply_after_spent_verification_holds_is_not_a_completion()
    {
        AssertBlankReplyIsEmptyReply(Snapshot(Failing(3).Concat(Notes(VerificationNote("process_exit_nonzero"), 3, 50))));
    }

    [Fact]
    public void R4d_tool_failure_beside_an_outstanding_check_completes_on_the_verification_bound()
    {
        var context = Snapshot(FailedRead(1).Concat(Failing(3))
            .Concat(Notes(VerificationNote("process_exit_nonzero"), 3, 50)));

        var decision = After(Step("All checks passed."), context);

        AssertDecision(decision, StepDecisionKind.Complete, "R4d.verification_advisory_complete");
    }

    [Fact]
    public void R4e_unverified_tool_failure_fails_with_the_receipt_code_once_the_notes_are_spent()
    {
        var context = Snapshot(FailedRead(1).Concat(Notes(AgentRecoveryNotes.BuildUnverifiedNote("edit_no_match"),
            AgentRecoveryNotes.MaximumUnverifiedNotes, 10)));

        var decision = After(Step("All done."), context);

        AssertDecision(decision, StepDecisionKind.UnverifiedFail, "R4e.tool_failure_fail");
        Assert.Equal("edit_no_match", decision.ErrorCode);
        Assert.True(decision.RetractStreamedText);
    }

    [Fact]
    public void R4_applies_to_a_blank_reply_after_a_failure_rather_than_the_empty_note()
    {
        var decision = After(Step("   "), Snapshot(FailedRead(1)));

        AssertDecision(decision, StepDecisionKind.Hold, "R4c.unverified_advice");
        Assert.Equal(AgentRecoveryKind.UnverifiedAfterToolFailure, decision.NoteKind);
    }

    // R5: unverified source edit.

    [Fact]
    public void R5_source_edit_awaiting_verification_is_held_once_with_the_nudge()
    {
        var decision = After(Step("Finished."), Snapshot(Edit(1)));

        AssertHold(decision, "R5.unverified_source_edit", AgentRecoveryKind.UnverifiedSourceEdit,
            AgentRecoveryNotes.BuildUnverifiedSourceEditNote());
        Assert.True(decision.RetractStreamedText);
    }

    [Fact]
    public void R5_spent_nudge_lets_the_prose_complete()
    {
        var context = Snapshot(Edit(1).Concat(Notes(AgentRecoveryNotes.BuildUnverifiedSourceEditNote(), 1, 10)));

        var decision = After(Step("Finished."), context);

        AssertDecision(decision, StepDecisionKind.Complete, "R9.complete");
    }

    // R6: open todos.

    [Fact]
    public void R6_open_todos_are_held_once_and_listed_in_the_note()
    {
        var decision = After(Step("Done."), Snapshot(TodoWrite(1, "write the parser")));

        AssertHold(decision, "R6.open_todos", AgentRecoveryKind.OpenTodos,
            AgentRecoveryNotes.BuildOpenTodosNote(new[] { "write the parser" }));
        Assert.True(decision.RetractStreamedText);
    }

    // R7: completion review.

    [Fact]
    public void R7_source_edit_with_a_passing_check_and_no_todos_is_held_for_one_review()
    {
        var decision = After(Step("Done."), Snapshot(Edit(1).Concat(Passing(3))));

        AssertHold(decision, "R7.completion_review", AgentRecoveryKind.CompletionReview,
            AgentRecoveryNotes.BuildCompletionReviewNote());
        Assert.True(decision.RetractStreamedText);
    }

    // R8: empty final reply.

    [Fact]
    public void R8_blank_reply_is_held_with_the_empty_note_after_one_prior_note()
    {
        var decision = After(Step("  "), Snapshot(Notes(AgentRecoveryNotes.BuildEmptyNote(), 1)));

        AssertHold(decision, "R8.empty_reply", AgentRecoveryKind.EmptyModelReply,
            AgentRecoveryNotes.BuildEmptyNote());
        Assert.Equal(1, decision.PriorNoteCount);
        Assert.True(decision.RetractStreamedText);
    }

    [Fact]
    public void R8_blank_reply_fails_with_its_code_once_three_notes_are_spent()
    {
        var context = Snapshot(Notes(AgentRecoveryNotes.BuildEmptyNote(), 3));

        var decision = After(Step(null), context);

        AssertDecision(decision, StepDecisionKind.Fail, "R8.empty_reply");
        Assert.Equal("empty_model_reply", decision.ErrorCode);
        Assert.True(decision.RetractStreamedText);
    }

    // R9: complete.

    [Fact]
    public void R9_passing_rerun_of_a_held_check_completes_without_another_hold()
    {
        var context = Snapshot(Failing(3).Concat(Notes(VerificationNote("process_exit_nonzero"), 1, 50)).Concat(Passing(5)));

        var decision = After(Step("Done."), context);

        Assert.Null(context.VerificationEvidence.BlockingCode);
        AssertDecision(decision, StepDecisionKind.Complete, "R9.complete");
    }

    [Fact]
    public void R9_plain_final_prose_completes_without_retraction()
    {
        var decision = After(Step("Done."), Empty());

        AssertDecision(decision, StepDecisionKind.Complete, "R9.complete");
        Assert.False(decision.RetractStreamedText);
        Assert.Null(decision.NoteKind);
    }

    // Ordering: the first applicable rule wins.

    [Fact]
    public void Blocked_reply_wins_over_the_unverified_source_edit_hold()
    {
        var decision = After(Step("[BLOCKED] I cannot finish."), Snapshot(Edit(1)));

        AssertDecision(decision, StepDecisionKind.BlockedReport, "R4a.blocked_reply");
    }

    [Fact]
    public void Outstanding_failed_check_wins_over_unverified_source_edit()
    {
        var decision = After(Step("Done."), Snapshot(Edit(1).Concat(Failing(3))));

        AssertHold(decision, "R4c.unverified_advice", AgentRecoveryKind.UnverifiedAfterToolFailure,
            VerificationNote("process_exit_nonzero"));
    }

    [Fact]
    public void Unverified_source_edit_wins_over_open_todos()
    {
        var decision = After(Step("Done."), Snapshot(Edit(1).Concat(TodoWrite(3, "write the parser"))));

        AssertDecision(decision, StepDecisionKind.Hold, "R5.unverified_source_edit");
    }

    [Fact]
    public void Open_todos_win_over_completion_review()
    {
        var decision = After(Step("Done."), Snapshot(Edit(1).Concat(Passing(3)).Concat(TodoWrite(5, "write the parser"))));

        AssertDecision(decision, StepDecisionKind.Hold, "R6.open_todos");
    }

    [Fact]
    public void Empty_text_is_never_held_by_a_prose_hold_awaiting_verification()
    {
        AssertBlankReplyIsEmptyReply(Snapshot(Edit(1)));
    }

    [Fact]
    public void Empty_text_is_never_held_by_the_open_todos_hold()
    {
        AssertBlankReplyIsEmptyReply(Snapshot(TodoWrite(1, "write the parser")));
    }

    [Fact]
    public void Empty_text_is_never_held_by_the_completion_review_hold()
    {
        AssertBlankReplyIsEmptyReply(Snapshot(Edit(1).Concat(Passing(3))));
    }

    private static void AssertBlankReplyIsEmptyReply(ModelContextSnapshot context)
    {
        var decision = After(Step(" "), context);

        AssertHold(decision, "R8.empty_reply", AgentRecoveryKind.EmptyModelReply,
            AgentRecoveryNotes.BuildEmptyNote());
    }

    private static StepDecision Before(AssistantStep step, ModelContextSnapshot context)
    {
        var decision = CompletionPolicy.DecideBeforeRefresh(step, context);
        Assert.NotNull(decision);
        return decision!;
    }

    private static StepDecision After(AssistantStep step, ModelContextSnapshot context) =>
        CompletionPolicy.DecideAfterRefresh(step, context);

    private static void AssertDecision(StepDecision decision, StepDecisionKind kind, string ruleName)
    {
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(ruleName, decision.RuleName);
    }

    private static void AssertHold(StepDecision decision, string ruleName, AgentRecoveryKind kind, string noteText)
    {
        AssertDecision(decision, StepDecisionKind.Hold, ruleName);
        Assert.Equal(kind, decision.NoteKind);
        Assert.Equal(noteText, decision.NoteText);
    }

    private static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) =>
        VerificationCompletionTests.Snapshot(entries);

    private static ModelContextSnapshot Empty() => Snapshot(Array.Empty<ModelContextEntry>());

    private static AssistantStep Step(string? text, AssistantFinishReason finish = AssistantFinishReason.Stop) =>
        new(text, Array.Empty<ModelToolCall>(), finish, 1);

    private static AssistantStep ToolStep(AssistantFinishReason finish = AssistantFinishReason.ToolCalls) =>
        new(null, [ReadCall()], finish, 1);

    private static ModelToolCall ReadCall() =>
        new("call-1", "read_file", "1.0.0", JsonSerializer.SerializeToElement(new { path = "src/a.cs" }));

    private static AssistantStep CommentaryStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 1,
            responsesMessages: Phase("commentary", text));

    private static ResponsesAssistantMessages Phase(string phase, string text = "Inspecting.") =>
        new([new JsonObject
        {
            ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["phase"] = phase,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
        }]);

    private static ModelContextEntry[] Notes(string text, int count, long firstSequence = 50) =>
        Enumerable.Range(0, count)
            .Select(index => ModelContextEntry.Text("note-" + (firstSequence + index), firstSequence + index,
                ModelContextRole.System, text))
            .ToArray();

    // The hold note for an outstanding verification failure, as the policy writes it.
    private static string VerificationNote(string code) => AgentRecoveryNotes.BuildUnverifiedNote(code, verificationBound: true);

    private static ModelContextEntry[] Edit(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "edit-" + sequence, "edit_file",
            """{"path":"src/a.cs","edits":[]}""", ToolExecutionOutcome.Succeeded, "ok", "{}");

    private static ModelContextEntry[] Passing(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");

    private static ModelContextEntry[] Failing(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");

    private static ModelContextEntry[] Mismatch(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "negative-" + sequence, "bash",
            """{"mode":"shell","script":"exit 1","purpose":"expected_failure","expected_exit_code":7}""",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");

    private static ModelContextEntry[] FailedRead(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "read-" + sequence, "edit_file",
            """{"path":"src/a.cs","edits":[]}""", ToolExecutionOutcome.Failed, "edit_no_match", "no match");

    private static ModelContextEntry[] TodoWrite(long sequence, params string[] pendingItems)
    {
        var json = JsonSerializer.Serialize(new
        {
            todos = pendingItems.Select(item => new { content = item, status = "pending" }).ToArray(),
        });
        return VerificationCompletionTests.Batch(sequence, "todo-" + sequence, "todo_write", json,
            ToolExecutionOutcome.Succeeded, "ok", json);
    }
}
