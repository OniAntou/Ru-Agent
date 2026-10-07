using System;

namespace RuApp.Services;

/// <summary>What the Escape key means at a given moment in a run.</summary>
public enum EscapeOutcome
{
    /// <summary>Nothing to cancel and no decision to answer.</summary>
    None,

    /// <summary>An approval is waiting. Answer it, and only it.</summary>
    DenyApproval,

    /// <summary>A run is in hand. Stop it.</summary>
    StopRun,
}

/// <summary>
/// Decides what Escape does during an agent run.
/// </summary>
/// <remarks>
/// Escape is the one key that reaches the app rather than the text field, so in a tool
/// that can execute a shell command it needs a stated meaning rather than an accidental
/// one. The rule is a precedence, and the order is the whole design:
///
/// <list type="number">
/// <item>An open approval outranks everything. A pending question has exactly one right
/// answer, and stopping the run instead of answering it punishes the user for a
/// decision they were asked to make. Denying is also the answer that costs least when it
/// is wrong: the agent asked again, nothing ran.</item>
/// <item>An open question outranks a run in hand too, but Escape does not answer it:
/// there is no deny, and stopping a run that is parked waiting for the answer would
/// discard the question. Escape is swallowed by the panel and the run keeps waiting.</item>
/// <item>Otherwise a run in hand means stop it.</item>
/// <item>Otherwise Escape means nothing at all, and is left to the focused control.</item>
/// </list>
///
/// The one place this is deliberately permissive is the composer. Escape stops the run
/// from there too, draft and all, because the composer stays live during a run by
/// design: Enter queues the next turn, so a user is expected to keep typing while the
/// agent works. Making Escape inert exactly when the user is watching the agent and
/// reaching for the keyboard would be the one moment it must not be. Nothing is lost
/// when it fires, because stopping a run leaves the draft where it was.
///
/// Stopping is irreversible for the turn, so the caller is expected to use the same
/// guard the stop button uses. Escape then does what clicking that button does, which is
/// the point of sharing it: two keys, one behaviour.
/// </remarks>
public static class AgentRunKeyboardPolicy
{
    /// <summary>
    /// Resolves Escape.
    /// </summary>
    /// <param name="runInHand">
    /// Whether this session has a run in hand, guarded exactly as the stop button is.
    /// </param>
    /// <param name="approvalShowing">Whether the approval prompt is on screen.</param>
    /// <param name="questionShowing">Whether the question prompt is on screen.</param>
    public static EscapeOutcome Resolve(bool runInHand, bool approvalShowing, bool questionShowing = false)
    {
        if (approvalShowing)
        {
            return EscapeOutcome.DenyApproval;
        }

        if (questionShowing)
        {
            return EscapeOutcome.None;
        }

        return runInHand ? EscapeOutcome.StopRun : EscapeOutcome.None;
    }
}
