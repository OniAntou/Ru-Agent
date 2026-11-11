using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// One-time soft holds for the completion checklist. Each hold is advice and is never a
/// failure path: the run continues whatever the model does next. Both are evaluated only
/// after the outstanding-check guard and the unverified-source-edit hold have declined, so
/// they never compete with a required check. A run pays at most one hold of each kind: a
/// fully completed checklist already is the review, so it suppresses the review hold.
/// </summary>
public static class CompletionChecklistGuard
{
    public static bool ShouldAdviseOpenTodos(ModelContextSnapshot context) =>
        context.VerificationEvidence.BlockingCode is null
        && context.VerificationEvidence.OpenTodoItems.Count > 0
        && AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.OpenTodos);

    /// <summary>
    /// The unverified-source-edit note already asks for the same requirement check, so this
    /// hold stays silent once that note has been issued. A run whose declared checks never
    /// failed pays no review either: the request, the edit and a first-time pass leave nothing
    /// the review would re-open, and the extra model round trip is the whole cost of it. A run
    /// that had to repair a failed check gets the review, because a repair is where a
    /// requirement is most often traded away to make the check pass.
    /// </summary>
    public static bool ShouldAdviseCompletionReview(ModelContextSnapshot context) =>
        context.VerificationEvidence.BlockingCode is null
        && context.VerificationEvidence.HasSourceEdits
        && context.VerificationEvidence.HadFailedDeclaredCheck
        && !context.VerificationEvidence.SourceEditAwaitingVerification
        && !context.VerificationEvidence.HasCompletedTodoList
        && AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.UnverifiedSourceEdit) == 0
        && AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.CompletionReview);
}
