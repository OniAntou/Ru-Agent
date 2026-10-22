using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ContextPanelPresenterTests
{
    [Fact]
    public void New_usage_without_breakdown_clears_the_previous_request_categories()
    {
        var meter = new RetainedContextMeterPresenter();
        var run = new AgentRunModel("run", "session", "workspace", "running", null, "standard", 1, "hash", 1, 0, "", "", null, null, null, null);
        meter.ScopeToRun(run, "session");
        var usage = new RuApp.Models.AgentModelUsage(900, null, null, null, null) { ContextTokens = new(100, 200, 600) };
        Assert.True(meter.ObserveUsage("run", "session", 1, "chat_completions", usage));
        Assert.NotNull(meter.ContextTokens);
        Assert.True(meter.ObserveUsage("run", "session", 2, "chat_completions", usage with { ContextTokens = null }));
        Assert.Null(meter.ContextTokens);
    }

    [Fact]
    public void Three_groups_keep_measured_values_and_put_only_draft_in_messages()
    {
        var reading = ContextPanelPresenter.Project(new(4700, 9000, 703000), 100, 999999, true);
        Assert.Equal(4700, reading.SystemWeight);
        Assert.Equal(9000, reading.ToolWeight);
        Assert.Equal(703100, reading.MessageWeight);
        Assert.StartsWith("~", reading.System);
        Assert.DoesNotContain("999", reading.Messages);
    }

    [Fact]
    public void Old_records_do_not_invent_system_or_tools_from_a_usage_gap()
    {
        var reading = ContextPanelPresenter.Project(null, 30, 120, true);
        Assert.Equal("—", reading.System);
        Assert.Equal("—", reading.Tools);
        Assert.Equal(120, reading.MessageWeight);
    }

    [Fact]
    public void Compaction_scope_and_out_of_order_frames_cannot_restore_an_old_breakdown()
    {
        var meter = new RetainedContextMeterPresenter();
        var run = new AgentRunModel("run", "session", "workspace", "running", null, "standard", 1, "hash", 1, 0, "", "", null, null, null, null);
        meter.ScopeToRun(run, "session");
        var estimate = new AgentUsageEstimateModel(900, 10000, "step", 5) { ContextTokens = new(100, 200, 600) };
        Assert.False(meter.ObserveEstimate("other", "session", estimate));
        Assert.True(meter.ObserveEstimate("run", "session", estimate));
        Assert.False(meter.ObserveEstimate("run", "session", estimate with { TransientSequence = 4 }));
        Assert.NotNull(meter.ContextTokens);
        meter.ObserveCompaction("run", "session", 20, 0);
        Assert.Null(meter.ContextTokens);
        Assert.False(meter.ObserveEstimate("run", "session", estimate));
    }
}
