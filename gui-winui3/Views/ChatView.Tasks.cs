using System;
using System.Threading.Tasks;
using System.Threading;
using RuApp.Models;
using RuApp.Services;
using RuAgent.Client;

namespace RuApp.Views;

/// <summary>
/// The right side surface's background-task and edit data. Rows come from the
/// Host's typed <c>/tasks</c> command and a stop is the same command with a kill
/// argument, so the Broker stays the only execution authority and the view
/// never talks to it directly. The summary card and the details panel render
/// the same rows; only the panel offers Stop.
/// </summary>
public sealed partial class ChatView
{
    private bool? _sidePanelRunRunning;
    private bool _sidePanelEditsDirty = true;
    private string? _sidePanelTasksRunId;
    private bool _taskRefreshInProgress;
    private readonly UiRequestLifetime _taskRefreshRequests = new();
    private CancellationTokenSource? _taskRefreshCancellation;

    /// <summary>
    /// Keeps the surface fresh from run-state updates: the title and edit
    /// projections retain unchanged rows. Visible task summaries also refresh
    /// periodically so processes can settle independently of the model's run.
    /// </summary>
    /// <summary>
    /// A newer title for the open session (one titled by its first prompt after it was
    /// opened). Titles for other sessions are ignored.
    /// </summary>
    public void UpdateSessionTitle(string sessionId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)
            || !string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)
            || string.Equals(title, _activeSessionTitle, StringComparison.Ordinal))
        {
            return;
        }

        _activeSessionTitle = title;
        SidePanel?.SetSessionTitle(title);
    }

    private void UpdateSidePanel(bool isRunRunning)
    {
        if (SidePanel is null)
        {
            return;
        }

        SidePanel.SetSessionTitle(_activeSessionTitle);
        SidePanel.SetWorkspace(_agentWorkspace);
        SidePanel.SetTerminalWorkingDirectory(_agentWorkspace);
        ApplySidePanelState();
        UpdateWelcomeWorkspaceText();
        RefreshSidePanelEdits();

        if (_sidePanelRunRunning != isRunRunning)
        {
            _sidePanelRunRunning = isRunRunning;
            _ = RefreshAgentTasksAsync();
        }
        UpdateTaskRefreshTimer(IsSidePanelDesiredVisible());
    }

    /// <summary>Rebuilds the Changes tab rows and the shared +added −removed totals.</summary>
    private void RefreshSidePanelEdits()
    {
        if (SidePanel is null || !_sidePanelEditsDirty)
        {
            return;
        }

        var projection = ChangesTabProjection.ProjectSession(Messages, AgentActivities);
        SidePanel.RenderChanges(projection.Rows, projection.Added, projection.Removed);
        _sidePanelEditsDirty = false;
    }

    private async Task RefreshAgentTasksAsync()
    {
        var version = _taskRefreshRequests.Replace();
        _taskRefreshInProgress = false;
        _taskRefreshCancellation?.Cancel();
        _taskRefreshCancellation?.Dispose();
        _taskRefreshCancellation = null;
        if (SidePanel is null)
        {
            return;
        }

        var runId = ResolveSidePanelTaskRunId();
        var controller = _agentController;
        var sessionGeneration = _sessionOpenGeneration;
        bool OwnsRefresh() => !_scrollingDisposed
            && _taskRefreshRequests.Owns(version)
            && sessionGeneration == _sessionOpenGeneration
            && ReferenceEquals(controller, _agentController)
            && string.Equals(runId, ResolveSidePanelTaskRunId(), StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(runId) || _agentController is null)
        {
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "No background tasks");
            return;
        }

        try
        {
            _taskRefreshInProgress = true;
            _taskRefreshCancellation = new CancellationTokenSource();
            var response = await controller!
                .DispatchCommandAsync(runId, "/tasks", _taskRefreshCancellation.Token)
                .ConfigureAwait(true);
            if (!OwnsRefresh()) return;
            if (!string.Equals(response.Status, "succeeded", StringComparison.Ordinal))
            {
                if (response.Code == "broker_gateway_unavailable" && runId == _sidePanelTasksRunId
                    && _agentController.CurrentRun?.RunId != runId)
                {
                    SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "No background tasks");
                    return;
                }
                SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), $"Tasks unavailable ({response.Code}).");
                return;
            }

            RenderAgentTasks(response.Data);
        }
        catch (AgentApiException error) when (error.ErrorCode == "broker_gateway_unavailable")
        {
            if (!OwnsRefresh()) return;
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "No background tasks");
        }
        catch (Exception)
        {
            if (!OwnsRefresh()) return;
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "Tasks unavailable; the host did not answer.");
        }
        finally
        {
            if (_taskRefreshRequests.Owns(version))
            {
                _taskRefreshInProgress = false;
                UpdateTaskRefreshTimer(IsSidePanelDesiredVisible());
            }
        }
    }

    private void RenderAgentTasks(object? data)
    {
        var projection = AgentTaskProjection.Project(data);
        SidePanel.RenderTasks(projection.Rows, projection.Status);
    }

}
