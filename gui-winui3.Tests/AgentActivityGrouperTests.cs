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

        // "Failed" alone left the reader unable to tell a stopped row from a refused one.
        Assert.Equal("Stopped before finishing", items[0].DisplayText);
        Assert.True(items[0].IsVisibleInFeed);
        Assert.Equal(AgentActivityState.Failed, items[1].State);
        Assert.Equal(AgentActivityState.Completed, items[2].State);
    }

    [Fact]
    public void Rows_with_different_counts_or_details_are_never_collapsed()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Search, symbol: "OnSearch", count: 3),
            Item(AgentActivityState.Completed, AgentActivityKind.Search, symbol: "OnSearch", count: 5),
            Item(AgentActivityState.Completed, AgentActivityKind.Edit, fileName: "a.txt", added: 1),
            Item(AgentActivityState.Completed, AgentActivityKind.Edit, fileName: "a.txt", added: 2),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.All(items, item => Assert.True(item.IsVisibleInFeed));
    }

    [Fact]
    public void Collapsing_folds_hidden_duration_into_the_survivor()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "LLM", durationMs: 19),
            Item(AgentActivityState.Completed, AgentActivityKind.Explore, fileName: "LLM", durationMs: 21),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.False(items[0].IsVisibleInFeed);
        Assert.Equal(40, items[1].EffectiveDurationMs);
    }

    [Fact]
    public void Settling_a_finished_run_stops_waiting_rows_too()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Waiting, AgentActivityKind.Edit, fileName: "obsolete.txt"),
        };

        AgentActivityGrouper.SettleRunningRows(items);

        Assert.Equal(AgentActivityState.Failed, items[0].State);
        Assert.Equal("interrupted", items[0].SafeCode);
    }

    private static AgentActivityItem Item(
        AgentActivityState state,
        AgentActivityKind kind,
        string? fileName = null,
        int? durationMs = null,
        string? symbol = null,
        int? count = null,
        int? added = null) =>
        new(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            kind,
            state,
            fileName,
            null,
            null,
            symbol,
            count,
            added,
            null,
            durationMs,
            "succeeded",
            null,
            null,
            null));
}
