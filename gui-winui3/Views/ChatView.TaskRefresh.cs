using System;
using System.Linq;
using Microsoft.UI.Dispatching;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private DispatcherQueueTimer? _sidePanelTaskTimer;

    private string? ResolveSidePanelTaskRunId() =>
        _agentController?.CurrentRun is { } run
            && string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                ? run.RunId : _sidePanelTasksRunId;

    private void UpdateTaskRefreshTimer(bool surfaceVisible)
    {
        bool hasLiveWork = _sidePanelRunRunning == true || SidePanel.Tasks.Any(row => row.State == "running");
        if (_scrollingDisposed || !surfaceVisible || !hasLiveWork || ResolveSidePanelTaskRunId() is null)
        {
            _sidePanelTaskTimer?.Stop();
            return;
        }

        if (_sidePanelTaskTimer is null)
        {
            _sidePanelTaskTimer = DispatcherQueue.CreateTimer();
            _sidePanelTaskTimer.Interval = TimeSpan.FromSeconds(2);
            _sidePanelTaskTimer.Tick += OnSidePanelTaskTimerTick;
        }
        if (!_sidePanelTaskTimer.IsRunning) _sidePanelTaskTimer.Start();
    }

    private async void OnSidePanelTaskTimerTick(DispatcherQueueTimer timer, object args)
    {
        if (!_taskRefreshInProgress) await RefreshAgentTasksAsync();
    }

    private async void OnSidePanelTasksRefreshRequested(object? sender, EventArgs args) =>
        await RefreshAgentTasksAsync();

    private void StopTaskRefreshTimer()
    {
        if (_sidePanelTaskTimer is null) return;
        _sidePanelTaskTimer.Stop();
        _sidePanelTaskTimer.Tick -= OnSidePanelTaskTimerTick;
        _sidePanelTaskTimer = null;
    }
}
