using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private bool _subagentsChipHovered;
    public event EventHandler? SubagentsChanged;
    public bool HasSubagents => SubagentBrowser.Snapshot is { } snapshot &&
        (snapshot.Agents.Count > 0 || snapshot.Preparations.Count > 0 || snapshot.UnavailableAttempts > 0);
    public string SubagentSummary { get; private set; } = "No subagents yet";

    private void InitializeSubagents()
    {
        SubagentBrowser.SnapshotChanged += OnSubagentSnapshotChanged;
        SubagentBrowser.CloseRequested += OnSubagentCloseRequested;
    }

    public void BindSubagents(ISubagentClient? client, string? root) => SubagentBrowser.Bind(client, root);
    public void SetSubagentOverlay(bool overlay) => FullscreenButton.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;

    private void OnSubagentSnapshotChanged(object? sender, EventArgs e)
    {
        var agents = SubagentBrowser.Snapshot?.Agents;
        var rows = agents?.Select(SubagentPresentation.Row).ToArray() ?? [];
        SubagentSummary = SubagentPresentation.CompactSummary(rows);
        if (rows.Length == 0 && SubagentBrowser.Snapshot is { } snapshot)
            SubagentSummary = snapshot.Preparations.Count > 0 ? $"Preparing {snapshot.Preparations.Count} task(s)"
                : snapshot.UnavailableAttempts > 0 ? "Tasks unavailable" : SubagentSummary;
        CardSubagentsSummary.Text = SubagentSummary;
        AutomationProperties.SetName(CardSubagentsRow, "View subagents, " + SubagentPresentation.Summary(rows));
        CardSubagentGlyphs.Children.Clear();
        foreach (var row in rows.OrderBy(r => r.Group).Take(4))
            CardSubagentGlyphs.Children.Add(new SubagentGlyph { AgentId = row.AgentId, Role = row.Role, Width = 24, Height = 24 });
        SubagentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCardSubagentsRowClick(object sender, RoutedEventArgs e) => OpenPanelTab(SidePanelTabKind.Subagents);
    private void OnSubagentsTabClick(object sender, RoutedEventArgs e) => TabActivated?.Invoke(this, SidePanelTabKind.Subagents);
    private void OnSubagentsTabCloseClick(object sender, RoutedEventArgs e) => TabClosed?.Invoke(this, SidePanelTabKind.Subagents);
    private void OnSubagentCloseRequested(object? sender, EventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    public void DisposeSubagents()
    {
        SubagentBrowser.SnapshotChanged -= OnSubagentSnapshotChanged;
        SubagentBrowser.CloseRequested -= OnSubagentCloseRequested;
        SubagentBrowser.Dispose();
    }
}
