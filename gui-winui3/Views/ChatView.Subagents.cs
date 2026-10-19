using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private bool IsSubagentOverlay() => RootGrid is not null && Messages.Count > 0 &&
        RootGrid.ActualWidth < SidePanelMinRootWidth &&
        _sidePanelPolicy.GetMode(_activeSessionId) == AgentSidePanelMode.Panel &&
        _sidePanelPolicy.GetActiveTab(_activeSessionId) == SidePanelTabKind.Subagents;

    private void OnSubagentsChanged(object? sender, EventArgs e) => UpdateSubagentPeek();

    private void UpdateSubagentPeek()
    {
        if (SubagentPeekButton is null) return;
        SubagentPeekButton.Visibility = SidePanel.HasSubagents && !CanShowSidePanel() && Messages.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        SubagentPeekText.Text = "Subagents · " + SidePanel.SubagentSummary;
    }

    private void OnSubagentPeekClick(object sender, RoutedEventArgs e) =>
        OnSidePanelTabOpenRequested(sender, SidePanelTabKind.Subagents);
}
