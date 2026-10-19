using System;
using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The right side surface's view logic: which host is showing for the active
/// session, how much width the transcript and composer leave for it, the
/// persisted width, and the events the panel control raises. The decisions live
/// in <see cref="AgentSidePanelPolicy"/>; the control only renders. The one
/// exception is the title-bar toggle's state, which MainWindow draws.
/// </summary>
public sealed partial class ChatView
{
    private const double SidePanelMargin = 16;
    private const double SidePanelCardWidth = 280;

    /// <summary>
    /// The side surface only shows when the chat area is at least this wide, so
    /// the transcript, composer and approval card keep a comfortable column
    /// after the surface reserves its space.
    /// </summary>
    private const double SidePanelMinRootWidth = 1120;

    private readonly AgentSidePanelPolicy _sidePanelPolicy = new();
    private readonly SidePanelPreferenceStore _sidePanelWidthStore = new();
    private double _sidePanelWidth;
    private double _sidePanelReserveWidth;

    private void InitializeSidePanel()
    {
        _sidePanelWidth = _sidePanelWidthStore.Load();
        SidePanel.SubagentsChanged += OnSubagentsChanged;
        SidePanel.ExpandRequested += OnSidePanelExpandRequested;
        SidePanel.TabOpenRequested += OnSidePanelTabOpenRequested;
        SidePanel.CollapseRequested += OnSidePanelCollapseRequested;
        SidePanel.FullscreenToggleRequested += OnSidePanelFullscreenToggleRequested;
        SidePanel.TabActivated += OnSidePanelTabActivated;
        SidePanel.TabClosed += OnSidePanelTabClosed;
        SidePanel.TabMoved += OnSidePanelTabMoved;
        SidePanel.TaskStopRequested += OnSidePanelTaskStopRequested;
        SidePanel.TasksRefreshRequested += OnSidePanelTasksRefreshRequested;
        SidePanel.WidthChanged += OnSidePanelWidthChanged;
        SidePanel.WidthCommitted += OnSidePanelWidthCommitted;
    }

    /// <summary>Hides or restores the whole surface; the title-bar toggle's action.</summary>
    public void ToggleSidePanel()
    {
        if (!CanShowSidePanel())
        {
            return;
        }

        _sidePanelPolicy.ToggleVisibility(_activeSessionId);
        ApplySidePanelState();
    }

    /// <summary>Drops a deleted session's surface state.</summary>
    public void ForgetSidePanelState(string sessionId) => _sidePanelPolicy.Remove(sessionId);

    private bool CanShowSidePanel() =>
        RootGrid is not null && Messages.Count > 0 && RootGrid.ActualWidth >= SidePanelMinRootWidth;

    /// <summary>The state the policy wants right now, re-checked after a fade-out.</summary>
    private bool IsSidePanelDesiredVisible() =>
        (CanShowSidePanel() || IsSubagentOverlay()) && _sidePanelPolicy.GetMode(_activeSessionId) != AgentSidePanelMode.Hidden;

    private void ApplySidePanelState()
    {
        double previousReserve = _sidePanelReserveWidth;
        UpdateSidePanelLayout();
        if (Math.Abs(previousReserve - _sidePanelReserveWidth) > 0.5)
        {
            ApplyInputWidth();
        }
    }

    private void UpdateSidePanelLayout()
    {
        if (SidePanel is null || RootGrid is null)
        {
            return;
        }

        bool canShow = CanShowSidePanel();
        var mode = _sidePanelPolicy.GetMode(_activeSessionId);
        // A narrow window temporarily suppresses presentation, not the user's
        // selected mode, tab or fullscreen state.

        bool overlay = IsSubagentOverlay();
        bool isVisible = (canShow || overlay) && mode != AgentSidePanelMode.Hidden;
        bool subagentReaderCoversChat = overlay || (mode == AgentSidePanelMode.Panel
            && _sidePanelPolicy.GetActiveTab(_activeSessionId) == SidePanelTabKind.Subagents
            && _sidePanelPolicy.IsFullscreen(_activeSessionId));
        // Cover the composer while retaining the main approval/question overlay's priority (20).
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(SidePanel, subagentReaderCoversChat ? 10 : 0);
        UpdateSubagentPeek();
        bool wasVisible = SidePanel.Visibility == Visibility.Visible;
        if (isVisible)
        {
            SidePanel.Visibility = Visibility.Visible;
            SidePanel.ApplyState(
                mode,
                _sidePanelPolicy.GetOpenTabs(_activeSessionId),
                _sidePanelPolicy.GetActiveTab(_activeSessionId),
                _sidePanelPolicy.IsFullscreen(_activeSessionId) || overlay,
                _sidePanelWidth);
            SidePanel.SetSubagentOverlay(overlay);
            if (!wasVisible)
            {
                // The control's own entrance only covers a mode *change*, and a
                // re-show after a hide repeats the same mode.
                SidePanel.PlayVisibilityEntrance();
            }
        }
        else if (wasVisible && !SidePanel.IsVisibilityExiting)
        {
            // The fade runs over the space the panel still reserves; the collapse
            // and the column's shift follow it, unless the surface was asked back
            // while it left.
            SidePanel.PlayVisibilityExit(() =>
            {
                if (IsSidePanelDesiredVisible())
                {
                    return;
                }

                SidePanel.Visibility = Visibility.Collapsed;
                ApplySidePanelState();
            });
        }

        // While the fade is in flight the reserve has to stay: the panel is still
        // painted there, so releasing the space early would jump the column under
        // it. The policy already reads Hidden, so the last rendered mode decides.
        _sidePanelReserveWidth = isVisible
            ? ReserveFor(mode)
            : SidePanel.Visibility == Visibility.Visible ? ReserveFor(SidePanel.CurrentMode) : 0;
        if (ApprovalHostGrid is not null)
        {
            ApprovalHostGrid.Margin = new Thickness(16, 0, 16 + _sidePanelReserveWidth, 24);
        }

        MainWindow.Instance?.UpdateSidePanelToggleState(canShow, mode == AgentSidePanelMode.Hidden);
        UpdateTaskRefreshTimer(isVisible);
    }

    private double ReserveFor(AgentSidePanelMode mode) =>
        mode switch
        {
            AgentSidePanelMode.Card => EffectiveSidePanelReserve(SidePanelCardWidth),
            AgentSidePanelMode.Panel when _sidePanelPolicy.IsFullscreen(_activeSessionId) || IsSubagentOverlay() => 0,
            AgentSidePanelMode.Panel => EffectiveSidePanelReserve(_sidePanelWidth),
            _ => 0,
        };

    /// <summary>
    /// How much width the surface needs to reserve: only the shift the centered
    /// column needs to clear the host (plus a breathing gap), so a wide window
    /// keeps the chat column centered instead of pushing it off to one side.
    /// </summary>
    private double EffectiveSidePanelReserve(double hostWidth)
    {
        if (RootGrid is null)
        {
            return 0;
        }

        double panelReserve = hostWidth + SidePanelMargin;
        double fullColumn = Math.Min(
            ReadingColumnMaxWidth,
            RootGrid.ActualWidth - HorizontalInsetFor(RootGrid.ActualWidth));
        double columnRight = (RootGrid.ActualWidth + fullColumn) / 2;
        double panelLeft = RootGrid.ActualWidth - panelReserve;
        double overlap = columnRight + 16 - panelLeft;
        return overlap <= 0 ? 0 : Math.Min(panelReserve, overlap * 2);
    }

    private void OnSidePanelExpandRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.ExpandToPanel(_activeSessionId);
        ApplySidePanelState();
        _ = RefreshAgentTasksAsync();
    }

    private void OnSidePanelTabOpenRequested(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.OpenTab(_activeSessionId, kind);
        _sidePanelPolicy.ExpandToPanel(_activeSessionId);
        ApplySidePanelState();
        if (kind == SidePanelTabKind.Tasks)
        {
            _ = RefreshAgentTasksAsync();
        }
    }

    private void OnSidePanelCollapseRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.CollapseToCard(_activeSessionId);
        ApplySidePanelState();
    }

    private void OnSidePanelFullscreenToggleRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.ToggleFullscreen(_activeSessionId);
        ApplySidePanelState();
    }

    private void OnSidePanelTabActivated(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.ActivateTab(_activeSessionId, kind);
        ApplySidePanelState();
        if (kind == SidePanelTabKind.Tasks)
        {
            _ = RefreshAgentTasksAsync();
        }
        else if (kind == SidePanelTabKind.Terminal)
        {
            SidePanel.RevealTerminal();
        }
    }

    private void OnSidePanelTabClosed(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.CloseTab(_activeSessionId, kind);
        ApplySidePanelState();
    }

    private void OnSidePanelTabMoved(object? sender, SidePanelTabMove move)
    {
        _sidePanelPolicy.MoveTab(_activeSessionId, move.Kind, move.TargetIndex);
        ApplySidePanelState();
    }

    private async void OnSidePanelTaskStopRequested(object? sender, string executionId)
    {
        if (_agentController is null)
        {
            return;
        }

        var runId = ResolveSidePanelTaskRunId();
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        long generation = _sessionOpenGeneration;
        var controller = _agentController;
        try
        {
            var result = await controller
                .DispatchCommandAsync(runId, "/tasks kill " + executionId)
                .ConfigureAwait(true);
            if (generation != _sessionOpenGeneration) return;
            if (!string.Equals(result.Status, "succeeded", StringComparison.Ordinal))
            {
                SidePanel.SetTasksStatus($"Could not stop task ({result.Code}). Refresh to check its state.");
                return;
            }
        }
        catch (Exception)
        {
            if (generation != _sessionOpenGeneration) return;
            SidePanel.SetTasksStatus("Stop failed; the host did not answer.");
            return;
        }

        await RefreshAgentTasksAsync();
    }

    private void OnSidePanelWidthChanged(object? sender, double width)
    {
        _sidePanelWidth = width;
        ApplySidePanelState();
    }

    private void OnSidePanelWidthCommitted(object? sender, double width)
    {
        _sidePanelWidth = width;
        _sidePanelWidthStore.TrySave(width);
    }
}
