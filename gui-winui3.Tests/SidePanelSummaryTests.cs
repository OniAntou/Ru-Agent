using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidePanelSummaryTests
{
    [Fact]
    public void Bounded_preview_keeps_running_processes_first_without_changing_full_observation()
    {
        AgentTaskRow[] rows = [new("done", "exited", 0), new("one", "running", null),
            new("two", "running", null), new("three", "running", null), new("last", "exited", 1)];
        Assert.Equal(["one", "two", "three"], SidePanelSummary.PreviewTasks(rows).Select(row => row.ExecutionId));
        Assert.Equal(5, rows.Length);
        Assert.Empty(SidePanelSummary.PreviewTasks([]));
    }

    [Fact]
    public void Summary_counts_recorded_edits_without_guessing_paths_from_basenames()
    {
        ChangesTabRow Row(string path) => new(path, 1, 0, "", "x", 1, "ok");
        Assert.Equal("No file changes", SidePanelSummary.ChangesLabel([]));
        Assert.Equal("1 file edit", SidePanelSummary.ChangesLabel([Row("a.cs")]));
        Assert.Equal("2 file edits", SidePanelSummary.ChangesLabel([Row("a.cs"), Row("a.cs")]));
    }

    [Fact]
    public void Stop_state_notifies_the_bound_action_without_changing_task_observation()
    {
        var row = new AgentTaskRow("task", "running", null, "npm test");
        var updates = new List<string?>();
        row.PropertyChanged += (_, e) => updates.Add(e.PropertyName);
        row.IsStopping = true;
        Assert.False(row.CanStop);
        Assert.Equal("Stopping…", row.StopText);
        Assert.Contains(nameof(AgentTaskRow.CanStop), updates);
        Assert.Contains(nameof(AgentTaskRow.StopText), updates);
        Assert.True(row.HasSameObservation(new("task", "running", null, "npm test")));
        row.IsStopping = false;
        Assert.True(row.CanStop);
        Assert.False(new AgentTaskRow("done", "exited", 0).CanStop);
    }
}
