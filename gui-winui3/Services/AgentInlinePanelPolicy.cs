namespace RuApp.Services;

/// <summary>
/// What the inline Agent panel above the composer should show for one moment.
///
/// The decisions used to be interleaved with the control assignments inside
/// ChatView.UpdateAgentInlineState, which meant the rules could only be checked by
/// reading that method. They live here so they can be asserted directly.
/// </summary>
/// <param name="ShowInlinePanel">The panel row itself is visible.</param>
/// <param name="ShowApproval">The approve/deny block is visible.</param>
/// <param name="ShowQuestion">The question block is visible.</param>
/// <param name="ShowStop">The stop control is visible.</param>
/// <param name="StatusText">Status line text, empty when it must be hidden.</param>
/// <param name="IsRunning">The send control reads as "running".</param>
/// <param name="SuppressInteraction">Approve/deny/stop are disabled while an action is in flight.</param>
public sealed record AgentInlinePanelState(
    bool ShowInlinePanel,
    bool ShowApproval,
    bool ShowQuestion,
    bool ShowStop,
    string StatusText,
    bool IsRunning,
    bool ShowWorkspaceTrust,
    bool SuppressInteraction);

/// <summary>The facts the inline panel is derived from; a plain projection of the controller.</summary>
public sealed record AgentInlinePanelInput(
    bool HasController,
    bool RunBelongsToAnotherSession,
    bool IsBusy,
    bool CanCancel,
    bool HasPendingApproval,
    bool HasPendingInput,
    bool NeedsWorkspaceTrust,
    bool CanContinue,
    bool KeepTerminalFailureVisible,
    bool RunPending,
    string StatusText,
    bool InteractionPending);

public static class AgentInlinePanelPolicy
{
    /// <summary>
    /// Hides everything. Used when there is no controller, when the run belongs to a
    /// session the user has navigated away from, and when the turn is finished with
    /// nothing left to act on.
    /// </summary>
    private static AgentInlinePanelState Hidden(AgentInlinePanelInput input) =>
        new(
            ShowInlinePanel: false,
            ShowApproval: false,
            ShowQuestion: false,
            ShowStop: false,
            StatusText: string.Empty,
            IsRunning: false,
            ShowWorkspaceTrust: false,
            SuppressInteraction: input.InteractionPending);

    public static AgentInlinePanelState Evaluate(AgentInlinePanelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Without a controller there is nothing to drive the panel from.
        if (!input.HasController)
        {
            return Hidden(input);
        }

        // A run owned by another session must not paint its state into this chat.
        if (input.RunBelongsToAnotherSession)
        {
            return Hidden(input);
        }

        // Nothing is running and nothing can be acted on: the panel collapses.
        if (!input.IsBusy
            && !input.CanCancel
            && !input.HasPendingApproval
            && !input.HasPendingInput
            && !input.NeedsWorkspaceTrust
            && !input.CanContinue
            && !input.KeepTerminalFailureVisible)
        {
            return Hidden(input);
        }

        bool isRunning = input.CanCancel || input.RunPending;

        if (input.NeedsWorkspaceTrust)
        {
            return new AgentInlinePanelState(
                ShowInlinePanel: true,
                ShowApproval: false,
                ShowQuestion: false,
                ShowStop: false,
                StatusText: input.StatusText,
                IsRunning: isRunning,
                ShowWorkspaceTrust: true,
                SuppressInteraction: input.InteractionPending);
        }

        // The panel is the actionable surface: an approval or question to answer,
        // a resume to send, or a terminal failure whose only signal lives here.
        bool hasInlineAction = input.HasPendingApproval
            || input.HasPendingInput
            || input.CanContinue
            || input.KeepTerminalFailureVisible;

        return new AgentInlinePanelState(
            ShowInlinePanel: hasInlineAction || !string.IsNullOrWhiteSpace(input.StatusText),
            ShowApproval: input.HasPendingApproval,
            ShowQuestion: input.HasPendingInput,
            ShowStop: false,
            StatusText: input.StatusText,
            IsRunning: isRunning,
            ShowWorkspaceTrust: false,
            SuppressInteraction: input.InteractionPending);
    }
}
