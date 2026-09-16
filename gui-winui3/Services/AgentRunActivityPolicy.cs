namespace RuApp.Services;

/// <summary>
/// Why the composer is considered to have a run in hand.
///
/// The reasons are distinct because they need different responses: <see cref="Sending"/>
/// is the window between tapping Send and the run existing, and only the view closes
/// it; the others are the controller's own signals.
/// </summary>
public enum AgentRunActivityReason
{
    None,
    Sending,
    Cancellable,
    AwaitingApproval,
    NeedsTrust,
}

/// <summary>
/// Everything the activity answer is derived from. A plain projection of the view's
/// send guard and the controller's public properties, taken at one moment.
/// </summary>
/// <param name="IsSendingTurn">The view is between tapping Send and the run existing.</param>
/// <param name="HasRun">The controller holds a run at all.</param>
/// <param name="RunSessionId">The session that run belongs to, when it has one.</param>
/// <param name="ActiveSessionId">The session the view is showing.</param>
/// <param name="CanCancel">The controller would accept a cancel.</param>
/// <param name="HasPendingApproval">The controller is holding a decision for the user.</param>
/// <param name="NeedsWorkspaceTrust">The controller is waiting to grant workspace trust.</param>
/// <param name="IsBusy">The controller's own busy flag.</param>
public sealed record AgentRunActivityInput(
    bool IsSendingTurn,
    bool HasRun,
    string? RunSessionId,
    string? ActiveSessionId,
    bool CanCancel,
    bool HasPendingApproval,
    bool NeedsWorkspaceTrust,
    bool IsBusy);

/// <summary>The answer, with the reason kept so a caller can explain itself.</summary>
public sealed record AgentRunActivityState(
    bool IsGuarded,
    bool ShowsStopAffordance,
    bool IsSessionBusy,
    AgentRunActivityReason Reason);

/// <summary>
/// Whether the client currently has a run in hand for the session on screen.
///
/// This question was previously answered by combining a private view flag with four
/// controller properties, and every call site concatenated them in its own order:
/// the send guard, the assistant-delta gate, the tool-output gate, the inline panel,
/// and the composer mode each spelled out their own subset. The rules below are the
/// ones those call sites relied on, now stated once.
/// </summary>
public static class AgentRunActivityPolicy
{
    /// <summary>
    /// A run counts for the session on screen only when it belongs to it. The
    /// controller can still hold the previous conversation's run while the view has
    /// already switched, and painting that run's state into the new chat is how a
    /// finished turn reappears in the wrong window.
    /// </summary>
    public static bool RunBelongsToSession(string? runSessionId, string? activeSessionId) =>
        runSessionId is not null
        && string.Equals(runSessionId, activeSessionId, StringComparison.Ordinal);

    public static AgentRunActivityState Evaluate(AgentRunActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The send window is not session-bound: it is the await between the tap and
        // the run being created, during which there is no run to compare against and
        // a second Enter would create a duplicate run and a duplicate user bubble.
        if (input.IsSendingTurn)
        {
            return new AgentRunActivityState(
                IsGuarded: true,
                ShowsStopAffordance: true,
                IsSessionBusy: true,
                Reason: AgentRunActivityReason.Sending);
        }

        bool bound = input.HasRun
            && RunBelongsToSession(input.RunSessionId, input.ActiveSessionId);

        if (!bound)
        {
            return new AgentRunActivityState(
                IsGuarded: false,
                ShowsStopAffordance: false,
                IsSessionBusy: false,
                Reason: AgentRunActivityReason.None);
        }

        // Ordered most-specific first, so the reason names the decision the user is
        // actually being asked for rather than the first flag that happened to be set.
        AgentRunActivityReason reason = input.HasPendingApproval
            ? AgentRunActivityReason.AwaitingApproval
            : input.NeedsWorkspaceTrust
                ? AgentRunActivityReason.NeedsTrust
                : input.CanCancel
                    ? AgentRunActivityReason.Cancellable
                    : AgentRunActivityReason.None;

        return new AgentRunActivityState(
            IsGuarded: reason != AgentRunActivityReason.None,
            ShowsStopAffordance: input.CanCancel,
            // A stream frame belongs to this session only while the controller is busy
            // on that same session; the controller stays busy through the previous
            // conversation's teardown, and painting those frames into the new chat is
            // how a finished turn reappears in the wrong window.
            IsSessionBusy: input.IsBusy,
            Reason: reason);
    }

    /// <summary>
    /// Whether the controller alone has a run for the session on screen. Needed by
    /// the send path itself, which already holds the send guard and must not use
    /// <see cref="Evaluate"/> or it would block its own caller.
    /// </summary>
    public static bool IsControllerRunPending(AgentRunActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input.HasRun
            && RunBelongsToSession(input.RunSessionId, input.ActiveSessionId)
            && (input.CanCancel || input.HasPendingApproval || input.NeedsWorkspaceTrust);
    }
}
