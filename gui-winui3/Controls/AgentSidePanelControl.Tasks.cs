using Microsoft.UI.Xaml;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private readonly HashSet<string> _stoppingTaskIds = new(StringComparer.Ordinal);

    public void SetTaskStopping(string executionId, bool stopping)
    {
        if (stopping) _stoppingTaskIds.Add(executionId);
        else _stoppingTaskIds.Remove(executionId);
        foreach (var row in Tasks.Where(row => row.ExecutionId == executionId)) row.IsStopping = stopping;
    }

    public void SetTasksRefreshing(bool refreshing)
    {
        TasksRefreshButton.IsEnabled = !refreshing;
        TasksLoadingRing.IsActive = refreshing;
        TasksLoadingRing.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
    }
}
