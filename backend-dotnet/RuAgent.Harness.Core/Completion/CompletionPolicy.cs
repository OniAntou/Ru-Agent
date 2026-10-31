using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core.Completion;

/// <summary>
/// The ordered, pure decision for a model step that returned. Rules are evaluated in the
/// listed order and the first rule that applies wins; no rule performs I/O. The coordinator
/// keeps the freshness check and evidence refresh between the two phases, so the pre-refresh
/// rules see the pinned step and the post-refresh rules see the refreshed verification evidence.
/// </summary>
internal static class CompletionPolicy
{
    // Reported when a [BLOCKED] reply has no failed receipt or outstanding check to name.
    private const string BlockedReplyFallbackCode = "model_reported_blocker";

    /// <summary>
    /// Phase A, before the freshness refresh: R1 unusable finish, R2 tool calls (returned as
    /// <see cref="StepDecisionKind.Proceed"/> so the coordinator dispatches them), R3 commentary-only.
    /// Null means no decision here, and the step continues to the refresh.
    /// </summary>
    public static StepDecision? DecideBeforeRefresh(AssistantStep step, ModelContextSnapshot context) =>
        UnusableFinish(step, context)
        ?? ToolCallDispatch(step)
        ?? CommentaryOnly(step, context);

    /// <summary>
    /// Phase B, after the freshness refresh and only for a step without tool calls:
    /// R4 failure or explicit blocker, R5 unverified source edit, R6 open todos,
    /// R7 completion review, R8 empty reply, R9 complete. R9 always matches.
    /// </summary>
    public static StepDecision DecideAfterRefresh(AssistantStep step, ModelContextSnapshot context) =>
        FailureOrBlocker(step, context)
        ?? UnverifiedSourceEdit(step, context)
        ?? OpenTodos(step, context)
        ?? CompletionReview(step, context)
        ?? EmptyReply(step, context)
        ?? new StepDecision(StepDecisionKind.Complete, "R9.complete");

    // R1: a truncated reply is held while notes remain; a filtered or errored reply always fails.
    // Partial tool calls in these replies are never dispatched, so the streamed text is retracted.
    private static StepDecision? UnusableFinish(AssistantStep step, ModelContextSnapshot context)
    {
        switch (step.FinishReason)
        {
            case AssistantFinishReason.Length:
                if (AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.OutputTruncated))
                {
                    return Hold("R1.unusable_finish", AgentRecoveryKind.OutputTruncated,
                        AgentRecoveryNotes.BuildTruncatedNote(), context, retract: true);
                }

                return TerminalReplyFailure("R1.unusable_finish", "model_output_truncated", retract: true);
            case AssistantFinishReason.ContentFilter:
                return TerminalReplyFailure("R1.unusable_finish", "model_content_filtered", retract: true);
            case AssistantFinishReason.Error:
                return TerminalReplyFailure("R1.unusable_finish", "model_reply_error", retract: true);
            default:
                return null;
        }
    }

    // R2: the coordinator keeps its repetition-guard and dispatch path; the policy only routes it.
    private static StepDecision? ToolCallDispatch(AssistantStep step) =>
        step.ToolCalls.Count > 0
            ? new StepDecision(StepDecisionKind.Proceed, "R2.tool_calls")
            : null;

    // R3: commentary is progress, kept in model history. Its streamed text stays visible.
    // Checked before the blocker rule, so a commentary-phased [BLOCKED] reply is never a blocker.
    private static StepDecision? CommentaryOnly(AssistantStep step, ModelContextSnapshot context)
    {
        if (step.ResponsesMessages?.IsCommentaryOnly != true)
        {
            return null;
        }

        return AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.CommentaryOnly)
            ? Hold("R3.commentary_only", AgentRecoveryKind.CommentaryOnly,
                AgentRecoveryNotes.BuildCommentaryNote(), context, retract: false, retainAssistantContext: true)
            : TerminalReplyFailure("R3.commentary_only", "model_commentary_only", retract: false);
    }

    // R4: a failed receipt, a pending or failed declared check, or a [BLOCKED] reply. An explicit
    // blocker is reported at once and never retried. A failure is held with a bounded note first. When
    // the notes are spent, a failure from verification evidence (BlockingCode) completes as unverified
    // (ADR 0039), and a failed tool receipt without verification evidence still fails. Blank text is
    // never a completion, so a blank reply after spent verification notes falls through to R8.
    private static StepDecision? FailureOrBlocker(AssistantStep step, ModelContextSnapshot context)
    {
        var failed = FailedToolCompletionGuard.TryGetFailure(context, out var failureCode);
        var blocked = AgentRecoveryNotes.TryReadBlockedReply(step, out var blockerReport);
        if (!failed && !blocked)
        {
            return null;
        }

        var errorCode = string.IsNullOrEmpty(failureCode) ? BlockedReplyFallbackCode : failureCode;
        if (blocked)
        {
            // A [BLOCKED] reply keeps its streamed text: the report is the model's own words.
            return BlockedReport("R4a.blocked_reply", blockerReport, errorCode, retract: false);
        }

        var verificationBound = context.VerificationEvidence.BlockingCode is not null;
        if (AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.UnverifiedAfterToolFailure))
        {
            return Hold("R4c.unverified_advice", AgentRecoveryKind.UnverifiedAfterToolFailure,
                AgentRecoveryNotes.BuildUnverifiedNote(errorCode, verificationBound), context, retract: true);
        }

        if (verificationBound)
        {
            // The completed reply is accepted as written, so its streamed text is not retracted.
            return IsBlank(step)
                ? null
                : new StepDecision(StepDecisionKind.Complete, "R4d.verification_advisory_complete");
        }

        return new StepDecision(StepDecisionKind.UnverifiedFail, "R4e.tool_failure_fail",
            RetractStreamedText: true, ErrorCode: errorCode);
    }

    // R5: one nudge after source edits with no declared verification since. Prose only.
    private static StepDecision? UnverifiedSourceEdit(AssistantStep step, ModelContextSnapshot context)
    {
        if (IsBlank(step) || !UnverifiedSourceEditGuard.ShouldAdvise(context))
        {
            return null;
        }

        return Hold("R5.unverified_source_edit", AgentRecoveryKind.UnverifiedSourceEdit,
            AgentRecoveryNotes.BuildUnverifiedSourceEditNote(), context, retract: true);
    }

    // R6: one nudge while the run's task list still has open items. Prose only.
    private static StepDecision? OpenTodos(AssistantStep step, ModelContextSnapshot context)
    {
        if (IsBlank(step) || !CompletionChecklistGuard.ShouldAdviseOpenTodos(context))
        {
            return null;
        }

        return Hold("R6.open_todos", AgentRecoveryKind.OpenTodos,
            AgentRecoveryNotes.BuildOpenTodosNote(context.VerificationEvidence.OpenTodoItems), context, retract: true);
    }

    // R7: one review after source edits and a passing check, when no checklist already is the review. Prose only.
    private static StepDecision? CompletionReview(AssistantStep step, ModelContextSnapshot context)
    {
        if (IsBlank(step) || !CompletionChecklistGuard.ShouldAdviseCompletionReview(context))
        {
            return null;
        }

        return Hold("R7.completion_review", AgentRecoveryKind.CompletionReview,
            AgentRecoveryNotes.BuildCompletionReviewNote(), context, retract: true);
    }

    // R8: a blank final reply gets one recovery note, then fails the run.
    private static StepDecision? EmptyReply(AssistantStep step, ModelContextSnapshot context)
    {
        if (!IsBlank(step))
        {
            return null;
        }

        return AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.EmptyModelReply)
            ? Hold("R8.empty_reply", AgentRecoveryKind.EmptyModelReply,
                AgentRecoveryNotes.BuildEmptyNote(), context, retract: true)
            : TerminalReplyFailure("R8.empty_reply", "empty_model_reply", retract: true);
    }

    private static bool IsBlank(AssistantStep step) => string.IsNullOrWhiteSpace(step.Text);

    private static StepDecision Hold(
        string ruleName,
        AgentRecoveryKind kind,
        string noteText,
        ModelContextSnapshot context,
        bool retract,
        bool retainAssistantContext = false) =>
        new(StepDecisionKind.Hold, ruleName,
            RetractStreamedText: retract,
            NoteKind: kind,
            NoteText: noteText,
            PriorNoteCount: AgentRecoveryNotes.NoteCount(context, kind),
            RetainAssistantContext: retainAssistantContext);

    private static StepDecision BlockedReport(string ruleName, string report, string errorCode, bool retract) =>
        new(StepDecisionKind.BlockedReport, ruleName,
            RetractStreamedText: retract, ErrorCode: errorCode, ReportText: report);

    private static StepDecision TerminalReplyFailure(string ruleName, string errorCode, bool retract) =>
        new(StepDecisionKind.Fail, ruleName, RetractStreamedText: retract, ErrorCode: errorCode);
}
