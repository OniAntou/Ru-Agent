using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityGrouperTests
{
    [Fact]
    public void Completed_thoughts_stay_in_the_feed_with_duration()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 4000),
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 4000),
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 3000),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.All(items, item => Assert.True(item.IsVisibleInFeed));
        Assert.Equal("Thought for 4 seconds", items[0].DisplayText);
    }

    [Fact]
    public void Running_thought_stays_visible_alongside_completed_ones()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 4000),
            Item(AgentActivityState.Started, AgentActivityKind.Thinking),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.True(items[0].IsVisibleInFeed);
        Assert.True(items[1].IsVisibleInFeed);
        Assert.Equal("Thinking", items[1].DisplayText);
    }

    [Fact]
    public void Identical_completed_tool_rows_keep_only_the_last()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "LLM", durationMs: 19),
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "LLM", durationMs: 21),
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "Docs", durationMs: 9),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.False(items[0].IsVisibleInFeed);
        Assert.True(items[1].IsVisibleInFeed);
        Assert.True(items[2].IsVisibleInFeed);
    }

    [Fact]
    public void Failed_and_waiting_rows_are_never_collapsed()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Failed, AgentActivityKind.Explore, fileName: "LLM"),
            Item(AgentActivityState.Waiting, AgentActivityKind.Edit, fileName: "obsolete.txt"),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.True(items[0].IsVisibleInFeed);
        Assert.True(items[1].IsVisibleInFeed);
    }

    [Fact]
    public void Settling_a_finished_run_stops_rows_that_never_completed()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Started, AgentActivityKind.Thinking),
            Item(AgentActivityState.Started, AgentActivityKind.Edit, fileName: "index.html"),
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "LLM", durationMs: 19),
        };

        AgentActivityGrouper.SettleRunningRows(items);
        AgentActivityGrouper.Reconcile(items);

        Assert.Equal(AgentActivityState.Failed, items[0].State);
        Assert.Equal("interrupted", items[0].SafeCode);
        Assert.Equal("Failed", items[0].DisplayText);
        Assert.True(items[0].IsVisibleInFeed);
        Assert.Equal(AgentActivityState.Failed, items[1].State);
        Assert.Equal(AgentActivityState.Completed, items[2].State);
    }

    private static AgentActivityItem Item(
        AgentActivityState state,
        AgentActivityKind kind,
        string? fileName = null,
        int? durationMs = null) =>
        new(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            kind,
            state,
            fileName,
            null,
            null,
            null,
            null,
            null,
            null,
            durationMs,
            "succeeded",
            null,
            null,
            null));
}
