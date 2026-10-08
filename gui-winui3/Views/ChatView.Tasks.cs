using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RuApp.Models;
using RuApp.Services;

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
    private const int MaxRenderedAgentTasks = 16;

    private bool? _sidePanelRunRunning;

    /// <summary>
    /// Keeps the surface fresh from run-state updates: the title and edit
    /// projections are cheap, and the task list refreshes only when the run
    /// starts or settles so the panel never polls while streaming.
    /// </summary>
    private void UpdateSidePanel(bool isRunRunning)
    {
        if (SidePanel is null)
        {
            return;
        }

        SidePanel.SetSessionTitle(_activeSessionTitle);
        SidePanel.SetWorkspace(_agentWorkspace);
        RefreshSidePanelEdits();

        if (_sidePanelRunRunning != isRunRunning)
        {
            _sidePanelRunRunning = isRunRunning;
            _ = RefreshAgentTasksAsync();
        }
    }

    /// <summary>Rebuilds the Changes tab rows and the shared +added −removed totals.</summary>
    private void RefreshSidePanelEdits()
    {
        if (SidePanel is null)
        {
            return;
        }

        var projection = ChangesTabProjection.Project(AgentActivities);
        SidePanel.RenderChanges(projection.Rows, projection.Added, projection.Removed);
    }

    private async Task RefreshAgentTasksAsync()
    {
        if (SidePanel is null)
        {
            return;
        }

        var runId = _agentController?.CurrentRun?.RunId ?? _activeAgentRunId;
        if (string.IsNullOrWhiteSpace(runId) || _agentController is null)
        {
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "No active run in this session.");
            return;
        }

        try
        {
            var response = await _agentController
                .DispatchCommandAsync(runId, "/tasks")
                .ConfigureAwait(true);
            if (!string.Equals(response.Status, "succeeded", StringComparison.Ordinal))
            {
                SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), $"Tasks unavailable ({response.Code}).");
                return;
            }

            RenderAgentTasks(response.Data);
        }
        catch (Exception)
        {
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "Tasks unavailable; the host did not answer.");
        }
    }

    private void RenderAgentTasks(object? data)
    {
        var rows = ParseAgentTasks(data);
        SidePanel.RenderTasks(
            rows,
            rows.Count == 0 ? "No background tasks for this session." : $"{rows.Count} task(s).");
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
