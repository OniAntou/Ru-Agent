using RuApp.Models;
using RuApp.Services;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuApp.Tests;

public sealed class SidePanelObservationTests
{
    [Fact]
    public void Task_outage_retains_identity_and_polling_until_an_authoritative_reply()
    {
        var observer = new BackgroundTaskObservation();
        var task = new AgentTaskRow("execution_one", "running", null, "synthetic command");
        observer.Apply(new([task], "1 running · 0 finished"));
        observer.Fail();
        Assert.Same(task, Assert.Single(observer.Rows));
        Assert.True(observer.ShouldPoll(false));
        Assert.Contains("last known", observer.Status);
        Assert.True(observer.RefreshInterval > TimeSpan.FromSeconds(2));
        for (int i = 0; i < 100; i++) observer.Fail();
        Assert.Equal(TimeSpan.FromSeconds(30), observer.RefreshInterval);
        observer.Apply(new([], "No background tasks"));
        Assert.Empty(observer.Rows);
        Assert.False(observer.ShouldPoll(false));
        Assert.Equal(TimeSpan.FromSeconds(2), observer.RefreshInterval);
    }

    [Fact]
    public void First_read_failure_retries_and_root_reset_drops_old_rows_and_failures()
    {
        var observer = new BackgroundTaskObservation();
        observer.Fail();
        Assert.True(observer.ShouldPoll(false));
        observer.Reset();
        Assert.False(observer.ShouldPoll(false));
        Assert.True(observer.ShouldPoll(true));
        Assert.Equal("No background tasks", observer.Status);
    }

    [Fact]
    public void Shared_subagent_summary_includes_mixed_state_and_refresh_freshness()
    {
        var agent = new SubagentSummary("agent_one", "review", "Review", false,
            [new("run_child", 1, "completed", 1, null, null, null, null)],
            [new(1, "run_child", "completed", null, null, null, true, null)]);
        var snapshot = new SubagentListResponse("root", "completed", "snapshot", [agent],
            [new("preparing_one", "preparing")], 1, new(0, 0, null, null, null, false));
        var text = SubagentPresentation.ObserverSummary(snapshot, false);
        Assert.Contains("1 preparing", text);
        Assert.Contains("1 unavailable", text);
        Assert.Contains("1 done", text);
        Assert.StartsWith("Refresh unavailable", SubagentPresentation.ObserverSummary(snapshot, true));
        Assert.DoesNotContain("Refresh unavailable", SubagentPresentation.ObserverSummary(snapshot, false));
        Assert.Equal("Refresh unavailable", SubagentPresentation.ObserverSummary(null, true));
    }
}
