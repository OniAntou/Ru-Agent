using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core.Completion;

/// <summary>
/// What the harness does with a model step once it has returned. Each kind maps to one
/// existing commit handler in <see cref="AgentRunStepHandlers"/>; the policy decides the
/// kind and the handler only commits it.
/// </summary>
internal enum StepDecisionKind
{
    /// <summary>Tool calls keep the coordinator's dispatch path (repetition guard, then execution).</summary>
    Proceed,

    /// <summary>Advice: one bounded recovery note is committed and the run keeps stepping.</summary>
    Hold,

    /// <summary>Terminal model-reply failure: unusable finish, commentary-only or empty reply.</summary>
    Fail,

    /// <summary>Terminal failure after a failed tool result with no note left and no verification evidence.</summary>
    UnverifiedFail,

    /// <summary>Explicit blocker: the report is visible and the run fails without claiming success.</summary>
    BlockedReport,

    /// <summary>
    /// The final reply is accepted and the run completes. After spent verification notes this
    /// completes with the summary verification state reported as unverified.
    /// </summary>
    Complete,
}

/// <summary>
/// One decided outcome. Only the fields the decided kind needs are set: an error code for
/// the terminal kinds, a report for <see cref="StepDecisionKind.BlockedReport"/>, and a
/// note kind, note text, prior note count and assistant-context flag for
/// <see cref="StepDecisionKind.Hold"/>.
/// </summary>
/// <param name="RetractStreamedText">True when streamed prose must be retracted before the commit.</param>
/// <param name="PriorNoteCount">Notes of the held kind already in the context; the retry activity counts from it.</param>
/// <param name="RetainAssistantContext">A held commentary step stays in model history instead of audit-only.</param>
internal sealed record StepDecision(
    StepDecisionKind Kind,
    string RuleName,
    bool RetractStreamedText = false,
    string? ErrorCode = null,
    string? ReportText = null,
    AgentRecoveryKind? NoteKind = null,
    string? NoteText = null,
    int PriorNoteCount = 0,
    bool RetainAssistantContext = false);
