using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityGrouperTests
{
    [Fact]
    public void Consecutive_completed_thoughts_fold_durations_into_the_last_row()
    {
        var items = new List<AgentActivityItem>
        {
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 4000),
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 4000),
            Item(AgentActivityState.Completed, AgentActivityKind.Thinking, durationMs: 3000),
        };

        AgentActivityGrouper.Reconcile(items);

        Assert.False(items[0].IsVisibleInFeed);
        Assert.False(items[1].IsVisibleInFeed);
        Assert.True(items[2].IsVisibleInFeed);
        Assert.Equal("Thought for 11 seconds", items[2].DisplayText);
    }

    [Fact]
    public void Running_thought_breaks_the_collapse_chain()
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
