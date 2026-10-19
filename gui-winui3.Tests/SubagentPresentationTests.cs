using System.Text.Json;
using RuAgent.Protocol.Subagents;
using RuApp.Services;
using Xunit;
namespace RuApp.Tests;
public sealed class SubagentPresentationTests
{
    private static SubagentSummary Agent(string status, string? code = null, bool publish = true) =>
        new("agent_one", "coding", "Fix the parser. Keep existing behavior.", false,
            [new("run_child", 1, status, 3, "provider", "model", "chat_completions", null, null)],
            publish ? [new(1, "run_child", status, code, null, null, false, null)] : []);

    [Theory]
    [InlineData("running", null, true, SubagentGroup.Working, "Working")]
    [InlineData("created", null, false, SubagentGroup.Working, "Queued")]
    [InlineData("completed", null, false, SubagentGroup.Working, "Collecting result")]
    [InlineData("completed", null, true, SubagentGroup.Finished, "Done")]
    [InlineData("cancelled", null, true, SubagentGroup.Finished, "Stopped")]
    [InlineData("failed", "provider_error", true, SubagentGroup.Attention, "Failed")]
    [InlineData("waiting_workspace_trust", null, false, SubagentGroup.Attention, "Waiting on main agent")]
    [InlineData("waiting_input", null, false, SubagentGroup.Attention, "Waiting on main agent")]
    [InlineData("waiting_history_final", null, false, SubagentGroup.Working, "Saving history")]
    [InlineData("waiting_subagents", null, false, SubagentGroup.Attention, "Waiting on delegated work")]
    [InlineData("future_status", null, false, SubagentGroup.Attention, "Status unavailable")]
    [InlineData("completed", "collection_failed", true, SubagentGroup.Attention, "Result unavailable")]
    public void Grouping_keeps_stopped_failed_and_unpublished_work_distinct(string status, string? code,
        bool publish, SubagentGroup group, string label)
    {
        var row = SubagentPresentation.Row(Agent(status, code, publish));
        Assert.Equal(group, row.Group);
        Assert.Equal(label, row.Status);
        Assert.Equal("Fix the parser.", row.Title);
    }

    [Fact]
    public void Version_and_root_navigation_reject_stale_result_reads()
    {
        var state = new SubagentTreeModel();
        state.Bind("root_one");
        var agent = Agent("completed") with { Results = [new(1, "run_child", "completed", null, null, null, false, null),
            new(2, "run_child_2", "completed", null, null, null, false, null)] };
        state.Apply(state.BeginRead(), new("root_one", "running", "snapshot", [agent], [], 0, new(0,0,null,null,null,false)));
        Assert.False(state.SelectAgent("agent_other"));
        Assert.True(state.SelectAgent("agent_one"));
        var latest = state.BeginResultRead();
        Assert.True(state.IsCurrent(latest));
        Assert.True(state.SelectVersion(1));
        Assert.False(state.IsCurrent(latest));
        Assert.False(state.SelectVersion(3));
        var old = state.BeginResultRead();
        state.ShowList();
        Assert.False(state.IsCurrent(old));
        state.SelectAgent("agent_one");
        old = state.BeginResultRead();
        state.Bind("root_two");
        Assert.False(state.IsCurrent(old));
        Assert.Null(state.SelectedAgentId);
    }

    [Fact]
    public void Summary_never_counts_stopped_as_success_or_incomplete_usage_as_a_total()
    {
        var rows = new[] { SubagentPresentation.Row(Agent("completed")),
            SubagentPresentation.Row(Agent("cancelled") with { AgentId = "agent_two" }) };
        Assert.Equal("1 done · 1 stopped", SubagentPresentation.Summary(rows));
    }
}
