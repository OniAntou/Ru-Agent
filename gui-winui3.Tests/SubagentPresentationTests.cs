using System.Text.Json;
using RuAgent.Protocol.Subagents;
using RuApp.Services;
using Xunit;
namespace RuApp.Tests;
public sealed class SubagentPresentationTests
{
    private static SubagentSummary Agent(string status, string? code = null, bool publish = true, bool detached = false) =>
        new("agent_one", "coding", "Fix the parser. Keep existing behavior.", false,
            [new("run_child", 1, status, 3, "provider", "model", "chat_completions", null, null)],
            publish ? [new(1, "run_child", status, code, null, null, false, null)] : [],
            Detached: detached);

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
    public void A_row_reports_elapsed_time_steps_tokens_and_profile_only_when_supplied()
    {
        var start = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
        var running = Agent("running", publish: false) with
        {
            Profile = "builder",
            Attempts = [new("run_child", 1, "running", 3, "provider", "model", "chat_completions", null, null,
                new(12, 12, 30000, 4500, 34500, true), start.AddSeconds(-5), start)],
        };

        var live = SubagentPresentation.Row(running, start.AddSeconds(252));
        Assert.Equal("Coding (builder) · Working · 4m 12s · 12 steps · 34.5k tokens", live.Subtitle);
        // Without a clock a live row is stable, so an unchanged snapshot renders the same text.
        Assert.Equal("Coding (builder) · Working · 12 steps · 34.5k tokens", SubagentPresentation.Row(running).Subtitle);

        var done = Agent("completed") with
        {
            Attempts = [new("run_child", 1, "completed", 3, "provider", "model", "chat_completions", null, null, null,
                start, start, start.AddHours(1).AddMinutes(5))],
        };
        Assert.Equal("Coding · Done · 1h 05m", SubagentPresentation.Row(done, start.AddDays(1)).Subtitle);
        // An older Host sends no instants: the row falls back to role and state.
        Assert.Equal("Coding · Done", SubagentPresentation.Row(Agent("completed")).Subtitle);
    }

    [Fact]
    public void A_ticking_duration_updates_the_row_in_place()
    {
        var first = SubagentPresentation.Row(Agent("running", publish: false));
        var second = SubagentPresentation.Row(Agent("running", publish: false));
        var changed = new List<string?>();
        first.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(first.SameState(second));
        first.Meta = "5s";

        Assert.Contains(nameof(SubagentRow.Subtitle), changed);
        Assert.True(first.SameState(second));
        Assert.False(first.SameState(SubagentPresentation.Row(Agent("failed"))));
    }

    [Fact]
    public void Polling_is_fast_while_work_is_live_slow_while_the_root_may_delegate_and_stops_when_settled()
    {
        static SubagentListResponse Snapshot(string root, params SubagentSummary[] agents) =>
            new("root", root, "snapshot", agents, [], 0, new(0, 0, null, null, null, false));

        Assert.Equal(SubagentPresentation.ActiveRefresh, SubagentPresentation.RefreshInterval(null));
        Assert.Equal(SubagentPresentation.ActiveRefresh, SubagentPresentation.RefreshInterval(Snapshot("running", Agent("running", publish: false))));
        // A finished attempt whose result is not published yet is still changing.
        Assert.Equal(SubagentPresentation.ActiveRefresh, SubagentPresentation.RefreshInterval(Snapshot("completed", Agent("completed", publish: false))));
        Assert.Equal(SubagentPresentation.IdleRefresh, SubagentPresentation.RefreshInterval(Snapshot("running", Agent("completed"))));
        Assert.Equal(SubagentPresentation.IdleRefresh, SubagentPresentation.RefreshInterval(Snapshot("completed", Agent("waiting_approval", publish: false))));
        Assert.Null(SubagentPresentation.RefreshInterval(Snapshot("completed", Agent("completed"), Agent("failed", "provider_error"))));
        Assert.Null(SubagentPresentation.RefreshInterval(Snapshot("cancelled")));
        // A failed or cancelled attempt's result is published on a later Host tick.
        Assert.Equal(SubagentPresentation.IdleRefresh, SubagentPresentation.RefreshInterval(Snapshot("cancelled", Agent("cancelled", publish: false))));
        // An interrupted run can be recovered under the same run id.
        Assert.Equal(SubagentPresentation.IdleRefresh, SubagentPresentation.RefreshInterval(Snapshot("interrupted", Agent("completed"))));
        Assert.Equal(SubagentPresentation.IdleRefresh, SubagentPresentation.RefreshInterval(Snapshot("completed", Agent("interrupted"))));
    }

    [Fact]
    public void Summary_never_counts_stopped_as_success_or_incomplete_usage_as_a_total()
    {
        var rows = new[] { SubagentPresentation.Row(Agent("completed")),
            SubagentPresentation.Row(Agent("cancelled") with { AgentId = "agent_two" }) };
        Assert.Equal("1 done · 1 stopped", SubagentPresentation.Summary(rows));
    }

    [Fact]
    public void A_detached_result_is_labelled_background_and_stays_ready_until_it_is_applied()
    {
        var pending = SubagentPresentation.Row(Agent("completed", detached: true));
        Assert.True(pending.Detached);
        Assert.True(pending.ResultReady);
        Assert.Equal("Coding · background · Done · result ready", pending.Subtitle);
        Assert.Contains("result ready", pending.AutomationLabel, StringComparison.Ordinal);

        var applied = Agent("completed", detached: true) with
        {
            Results = [new(1, "run_child", "completed", null, "applied", 3, false, null)],
        };
        var row = SubagentPresentation.Row(applied);
        Assert.True(row.Detached);
        Assert.False(row.ResultReady);
        Assert.Equal("Coding · background · Done", row.Subtitle);

        var snapshot = new SubagentListResponse("root_run", "completed", "snapshot",
            [Agent("completed", detached: true)], [], 0, new(0, 0, null, null, null, false));
        Assert.Contains("1 result ready", SubagentPresentation.ObserverSummary(snapshot, false), StringComparison.Ordinal);
    }
}
