using RuApp.Controls;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private string? _focusedChangeActivityId;
    private bool _changesPanelExplicitlyOpened;

    private void OnTurnChangesRequested(object? sender, TurnChangesRequestedEventArgs args)
    {
        var owner = args.Message;
        if (_isSessionHistoryLoading || !Messages.Contains(owner)
            || !string.Equals(owner.SessionId, _activeSessionId, StringComparison.Ordinal)) return;
        var summary = TurnChangesProjection.ForMessage(owner);
        if (summary.Files.Count == 0) return;

        _focusedChangeActivityId = summary.Files.FirstOrDefault(file =>
            string.Equals(file.Path, args.FilePath, StringComparison.OrdinalIgnoreCase))?.Edits.LastOrDefault()?.ActivityId;
        _changesPanelExplicitlyOpened = true;
        _sidePanelEditsDirty = true;
        RefreshSidePanelEdits();
        OpenSidePanelTab(SidePanelTabKind.Changes);
        SidePanel.RevealChange(_focusedChangeActivityId);
    }

    private void ClearChangeNavigation()
    {
        _focusedChangeActivityId = null;
        _sidePanelEditsDirty = true;
        SidePanel.RevealChange(null);
    }

    private bool IsTurnChangesOverlay() => _changesPanelExplicitlyOpened && RootGrid is not null
        && Messages.Count > 0 && RootGrid.ActualWidth < SidePanelMinRootWidth
        && _sidePanelPolicy.GetMode(_activeSessionId) == AgentSidePanelMode.Panel
        && _sidePanelPolicy.GetActiveTab(_activeSessionId) == SidePanelTabKind.Changes;

    private bool IsSidePanelOverlay() => IsSubagentOverlay() || IsTurnChangesOverlay();
}
