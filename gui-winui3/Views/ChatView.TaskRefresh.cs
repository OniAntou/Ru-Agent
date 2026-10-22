using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using RuApp.Services;

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
        bool hasLiveWork = _taskObservation.ShouldPoll(_sidePanelRunRunning == true);
        if (_scrollingDisposed || !surfaceVisible || !hasLiveWork || ResolveSidePanelTaskRunId() is null)
        {
            _sidePanelTaskTimer?.Stop();
            return;
        }

        if (_sidePanelTaskTimer is null)
        {
            _sidePanelTaskTimer = DispatcherQueue.CreateTimer();
            _sidePanelTaskTimer.Tick += OnSidePanelTaskTimerTick;
        }
        _sidePanelTaskTimer.Interval = _taskObservation.RefreshInterval;
        if (!_sidePanelTaskTimer.IsRunning) _sidePanelTaskTimer.Start();
    }

    private async void OnSidePanelTaskTimerTick(DispatcherQueueTimer timer, object args)
    {
        try
        {
            if (!_taskRefreshInProgress) await RefreshAgentTasksAsync();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Side panel task tick failed: {ex.Message}");
        }
    }

    private async void OnSidePanelTasksRefreshRequested(object? sender, EventArgs args)
    {
        try
        {
            await RefreshAgentTasksAsync();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Side panel task refresh failed: {ex.Message}");
        }
    }

    private void StopTaskRefreshTimer()
    {
        if (_sidePanelTaskTimer is null) return;
        _sidePanelTaskTimer.Stop();
        _sidePanelTaskTimer.Tick -= OnSidePanelTaskTimerTick;
        _sidePanelTaskTimer = null;
    }
}
