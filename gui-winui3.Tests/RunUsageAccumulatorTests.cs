using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RunUsageAccumulatorTests
{
    [Fact]
    public void All_step_usage_is_summed_while_peak_stays_an_individual_request()
    {
        var usage = RunUsageAccumulator.Sum([
            new AgentModelUsage(1000, 10, 1010, 998, 0, "model", true),
            new AgentModelUsage(2000, 20, 2020, 1800, 0, "model", true),
        ])!;
        Assert.Equal(202, usage.InputTokens);
        Assert.Equal(2798, usage.CacheReadInputTokens);
        Assert.Equal(3030, usage.ResolvedTotal);
        Assert.Equal(2020, usage.PeakStepTokens);
        Assert.Equal(2798 * 100.0 / 3000, usage.CacheHitPercent!.Value, 6);
        var summary = SessionUsagePolicy.Summarize([new SessionUsageRow("run", true, "completed", null, null, usage)]);
        Assert.Equal(3030, summary.BilledTokens);
        Assert.Equal(2020, summary.PeakTotal);
    }

    [Fact]
    public void Missing_accounting_is_unknown_and_replayed_payload_references_are_not_counted_twice()
    {
        Assert.Null(RunUsageAccumulator.Sum([new AgentModelUsage(100, 10, null, 99, null)] )!.CacheHitPercent);
        var events = new[]
        {
            """{"event_type":"model.usage_recorded","payload_ref":"one"}""",
            """{"event_type":"model.usage_recorded","payload_ref":"one"}""",
            """{"event_type":"model.usage_recorded","payload_ref":"two"}""",
        };
        Assert.Equal(["one", "two"], SessionUsageReplayPolicy.FindUsagePayloadRefs(events));
        Assert.Equal("two", SessionUsageReplayPolicy.FindLatestUsagePayloadRef(events));
    }
}
