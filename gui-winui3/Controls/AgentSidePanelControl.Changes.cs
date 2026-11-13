using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;
using Windows.ApplicationModel.DataTransfer;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private int _changeRevealGeneration;
    private IReadOnlyList<ChangesTabRow> _allChangeRows = Array.Empty<ChangesTabRow>();
    private bool _showScratchChanges;

    public void RevealChange(string? activityId)
    {
        int generation = ++_changeRevealGeneration;
        // A revealed scratch file must be visible to scroll to.
        if (!_showScratchChanges && _allChangeRows.FirstOrDefault(change => change.Contains(activityId)) is { IsScratch: true })
        {
            _showScratchChanges = true;
            ApplyChangeRows();
        }

        var row = Changes.FirstOrDefault(change => change.Contains(activityId));
        if (row is null) return;
        row.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _changeRevealGeneration || !Changes.Contains(row)
                || _mode != AgentSidePanelMode.Panel || _activeTab != SidePanelTabKind.Changes) return;
            ChangesList.UpdateLayout();
            if (ChangesList.ContainerFromItem(row) is FrameworkElement container)
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
    }

    public void RenderChanges(IReadOnlyList<ChangesTabRow> rows, int added, int removed)
    {
        _allChangeRows = rows;
        ApplyChangeRows();
        CardChangesLabel.Text = SidePanelSummary.ChangesLabel(rows);
        CardChangeDeltas.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // One number format for the totals and the rows below them.
        CardAddedText.Text = PanelAddedText.Text = TurnChangesProjection.FormatCount(added, "+");
        CardRemovedText.Text = PanelRemovedText.Text = TurnChangesProjection.FormatCount(removed, "−");
        ChangesSummaryText.Text = SidePanelSummary.ChangesLabel(rows);
    }

    /// <summary>Shows the rows, holding scratch files back unless the user asked for them.</summary>
    private void ApplyChangeRows()
    {
        int scratch = _allChangeRows.Count(row => row.IsScratch);
        var visible = _showScratchChanges ? _allChangeRows : _allChangeRows.Where(row => !row.IsScratch).ToArray();
        ChangesTabReconciler.Apply(Changes, visible);

        ShowScratchToggle.Visibility = scratch > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowScratchToggle.IsChecked = _showScratchChanges;
        ShowScratchToggle.Content = scratch == 1 ? "1 scratch file" : $"{scratch} scratch files";
        ChangesEmptyDetail.Text = _allChangeRows.Count > 0
            ? "Only scratch files changed. Show them with the button above."
            : "Files the agent edits in this session appear here.";
        ChangesEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesList.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// A row's actions button is drawn only while the pointer or the keyboard is on the row.
    /// It stays in the tab order and the automation tree throughout.
    /// </summary>
    private const double ChangeRowActionsWidth = 28;

    private static void SetChangeRowActive(object sender, bool active)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Grid { Children.Count: > 1 } row
            || row.Children[1] is not Microsoft.UI.Xaml.Controls.Button actions) return;
        // An open actions menu takes the pointer away; the button stays until it closes.
        if (!active && actions.Flyout?.IsOpen == true)
        {
            void OnClosed(object? source, object args)
            {
                actions.Flyout.Closed -= OnClosed;
                SetChangeRowActive(row, false);
            }

            actions.Flyout.Closed += OnClosed;
            return;
        }

        // No hover surface: sweeping the pointer down the list lit and dimmed each row in
        // turn, which read as flicker. The counts moving aside for the button is the cue.
        UiMotion.FadeOpacity(actions, active ? 1 : 0);
        if (row.Children.Count > 2 && row.Children[2] is FrameworkElement counts)
            UiMotion.SlideX(counts, active ? -ChangeRowActionsWidth : 0);
    }

    // The row's Tag remembers whether the pointer is over it, so losing keyboard focus
    // under a resting pointer (a click elsewhere in the row) does not hide the button.
    private void OnChangeRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row) row.Tag = true;
        SetChangeRowActive(sender, true);
    }

    private void OnChangeRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row) row.Tag = false;
        SetChangeRowActive(sender, false);
    }

    private void OnChangeRowGotFocus(object sender, RoutedEventArgs e)
    {
        // A click also focuses the row; only keyboard focus needs the row lit without a pointer.
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Control { FocusState: FocusState.Keyboard })
            SetChangeRowActive(sender, true);
    }

    private void OnChangeRowLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: true }) return;
        SetChangeRowActive(sender, false);
    }

    private void OnChangeRowCountsTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChangesTabRow row }) row.IsExpanded = !row.IsExpanded;
    }

    private void OnShowScratchClick(object sender, RoutedEventArgs e)
    {
        _showScratchChanges = ShowScratchToggle.IsChecked == true;
        ApplyChangeRows();
    }

    private async void OnChangeOpenPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ChangesTabRow row }
                || ChangesTabFiles.Resolve(_workspacePath, row.RelativePath) is not { } path
                || _workspacePath is not { } workspace) return;
            OpenPanelTab(SidePanelTabKind.Preview);
            await FilePreview.OpenAsync(workspace, path);
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[AgentSidePanel] Change preview failed: {ex.Message}");
        }
    }

    private void OnChangeRevealClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChangesTabRow row }
            || ChangesTabFiles.Resolve(_workspacePath, row.RelativePath) is not { } path) return;
        try
        {
            // A deleted file has no item to select; open its folder instead.
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[AgentSidePanel] Reveal in Explorer failed: {ex.Message}");
        }
    }

    private void OnChangeCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChangesTabRow row }) return;
        var package = new DataPackage();
        package.SetText(row.RelativePath ?? row.FileName);
        Clipboard.SetContent(package);
    }
}
