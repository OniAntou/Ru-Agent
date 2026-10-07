using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Views;

/// <summary>
/// The right side panel's background-task section. Rows come from the Host's
/// typed <c>/tasks</c> command and a stop is the same command with a kill
/// argument, so the Broker stays the only execution authority and the view
/// never talks to it directly. The panel also carries the session's running
/// edit totals, summed from the transcript's edit rows.
/// </summary>
public sealed partial class ChatView
{
    private const int MaxRenderedAgentTasks = 16;

    /// <summary>Rows the panel binds to; rebuilt on every refresh.</summary>
    public ObservableCollection<AgentTaskRow> AgentTasks { get; } = new();

    private bool? _sidePanelRunRunning;

    /// <summary>
    /// Keeps the panel fresh from run-state updates: the title and edit totals
    /// are cheap, and the task list refreshes only when the run starts or
    /// settles so the panel never polls while streaming.
    /// </summary>
    private void UpdateSidePanel(bool isRunRunning)
    {
        if (SidePanelCard is null)
        {
            return;
        }

        SidePanelTitleText.Text = _activeSessionTitle;
        RefreshSidePanelEdits();

        if (_sidePanelRunRunning != isRunRunning)
        {
            _sidePanelRunRunning = isRunRunning;
            _ = RefreshAgentTasksAsync();
        }
    }

    /// <summary>Sum of the transcript's edit rows, shown as +added −removed.</summary>
    private void RefreshSidePanelEdits()
    {
        if (SidePanelAddedText is null || SidePanelRemovedText is null)
        {
            return;
        }

        int added = 0;
        int removed = 0;
        foreach (var activity in AgentActivities)
        {
            if (activity.Kind != AgentActivityKind.Edit)
            {
                continue;
            }

            added += activity.Added ?? 0;
            removed += activity.Removed ?? 0;
        }

        SidePanelAddedText.Text = "+" + added;
        SidePanelRemovedText.Text = "−" + removed;
    }

    private void OnSidePanelCollapseClick(object sender, RoutedEventArgs e)
    {
        if (SidePanelCard is null || SidePanelOpenButton is null)
        {
            return;
        }

        SidePanelCard.Visibility = Visibility.Collapsed;
        SidePanelOpenButton.Visibility = Visibility.Visible;
    }

    private void OnSidePanelOpenClick(object sender, RoutedEventArgs e)
    {
        if (SidePanelCard is null || SidePanelOpenButton is null)
        {
            return;
        }

        SidePanelCard.Visibility = Visibility.Visible;
        SidePanelOpenButton.Visibility = Visibility.Collapsed;
        _ = RefreshAgentTasksAsync();
    }

    private async void OnAgentTasksRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAgentTasksAsync();
    }

    private async void OnAgentTaskStopClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is not string executionId
            || _agentController is null)
        {
            return;
        }

        var runId = _agentController.CurrentRun?.RunId ?? _activeAgentRunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        try
        {
            await _agentController
                .DispatchCommandAsync(runId, "/tasks kill " + executionId)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            AgentTasksStatusText.Text = "Stop failed; the host did not answer.";
            return;
        }

        await RefreshAgentTasksAsync();
    }

    private async Task RefreshAgentTasksAsync()
    {
        if (AgentTasksList is null || AgentTasksStatusText is null)
        {
            return;
        }

        var runId = _agentController?.CurrentRun?.RunId ?? _activeAgentRunId;
        if (string.IsNullOrWhiteSpace(runId) || _agentController is null)
        {
            AgentTasks.Clear();
            AgentTasksStatusText.Text = "No active run in this session.";
            return;
        }

        try
        {
            var response = await _agentController
                .DispatchCommandAsync(runId, "/tasks")
                .ConfigureAwait(true);
            if (!string.Equals(response.Status, "succeeded", StringComparison.Ordinal))
            {
                AgentTasks.Clear();
                AgentTasksStatusText.Text = $"Tasks unavailable ({response.Code}).";
                return;
            }

            RenderAgentTasks(response.Data);
        }
        catch (Exception)
        {
            AgentTasks.Clear();
            AgentTasksStatusText.Text = "Tasks unavailable; the host did not answer.";
        }
    }

    private void RenderAgentTasks(object? data)
    {
        AgentTasks.Clear();
        var rows = ParseAgentTasks(data);
        if (rows.Count == 0)
        {
            AgentTasksStatusText.Text = "No background tasks for this session.";
            return;
        }

        foreach (var row in rows)
        {
            AgentTasks.Add(row);
        }

        AgentTasksStatusText.Text = $"{rows.Count} task(s).";
    }

    private static List<AgentTaskRow> ParseAgentTasks(object? data)
    {
        var rows = new List<AgentTaskRow>();
        if (data is not JsonElement element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (rows.Count >= MaxRenderedAgentTasks)
            {
                break;
            }

            if (task.ValueKind != JsonValueKind.Object
                || !task.TryGetProperty("execution_id", out var idProperty)
                || idProperty.GetString() is not { Length: > 0 } executionId)
            {
                continue;
            }

            var state = task.TryGetProperty("state", out var stateProperty)
                ? stateProperty.GetString() ?? "unknown"
                : "unknown";
            int? exitCode = task.TryGetProperty("exit_code", out var exitProperty)
                && exitProperty.ValueKind == JsonValueKind.Number
                && exitProperty.TryGetInt32(out var parsedExit)
                    ? parsedExit
                    : null;
            rows.Add(new AgentTaskRow(executionId, state, exitCode));
        }

        return rows;
    }
}
