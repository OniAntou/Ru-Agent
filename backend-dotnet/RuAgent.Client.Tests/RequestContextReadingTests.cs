using Xunit;

namespace RuAgent.Client.Tests;

public sealed class RequestContextReadingTests
{
    [Fact]
    public void Reported_input_replaces_estimate_and_late_estimate_cannot_restore_it()
    {
        var reading = new RequestContextReading();
        Assert.True(reading.BeginStep("a", 1));
        Assert.True(reading.ObserveEstimate(Estimate("a", 80_000, 10)));
        Assert.True(reading.IsEstimated);
        Assert.True(reading.ObserveUsage(2, "a", 50_000, null));
        Assert.False(reading.ObserveEstimate(Estimate("a", 80_000, 11)));
        Assert.Equal(50_000, reading.Tokens);
        Assert.True(reading.HasReportedUsage);
    }

    [Fact]
    public void Usage_before_buffered_estimate_still_wins_and_new_step_accepts_reset_transient_cursor()
    {
        var reading = new RequestContextReading();
        Assert.True(reading.ObserveUsage(10, "a", 50_000, null));
        Assert.False(reading.ObserveEstimate(Estimate("a", 80_000, 100)));
        Assert.True(reading.BeginStep("b", 11));
        Assert.True(reading.ObserveEstimate(Estimate("b", 55_000, 1)));
        Assert.False(reading.ObserveUsage(12, "a", 90_000, null));
        Assert.False(reading.ObserveEstimate(Estimate("a", 80_000, 101)));
        Assert.True(reading.ObserveUsage(13, "b", 52_000, null));
        Assert.Equal(52_000, reading.Tokens);
    }

    [Fact]
    public void Compaction_retires_precompaction_step_and_waits_for_next_durable_step()
    {
        var reading = new RequestContextReading();
        reading.BeginStep("a", 1);
        reading.ObserveEstimate(Estimate("a", 80_000, 2));
        Assert.True(reading.Compact(3));
        Assert.Null(reading.Tokens);
        Assert.False(reading.ObserveEstimate(Estimate("a", 80_000, 4)));
        Assert.False(reading.ObserveEstimate(Estimate("b", 10_000, 5)));
        Assert.False(reading.ObserveUsage(2, "a", 80_000, null));
        Assert.True(reading.BeginStep("b", 4));
        Assert.True(reading.ObserveEstimate(Estimate("b", 10_000, 1)));
        Assert.Equal(10_000, reading.Tokens);
    }

    [Fact]
    public void Missing_input_does_not_become_zero_and_does_not_allow_late_estimate()
    {
        var reading = new RequestContextReading();
        reading.ObserveEstimate(Estimate("a", 100, 1));
        reading.ObserveUsage(2, "a", null, null);
        Assert.Equal(100, reading.Tokens);
        Assert.True(reading.IsEstimated);
        Assert.False(reading.ObserveEstimate(Estimate("a", 200, 2)));
    }

    [Fact]
    public void Legacy_usage_and_zero_input_remain_supported_without_a_step_id()
    {
        var reading = new RequestContextReading();
        Assert.True(reading.ObserveUsage(1, null, 0, null));
        Assert.Equal(0, reading.Tokens);
        Assert.True(reading.HasReportedUsage);
        Assert.False(reading.ObserveUsage(1, null, 500, null));
        Assert.True(reading.ObserveUsage(2, null, 500, null));
        Assert.Equal(500, reading.Tokens);
    }

    [Fact]
    public void Restore_cannot_overwrite_live_data_but_reset_allows_a_new_run()
    {
        var reading = new RequestContextReading();
        reading.ObserveUsage(20, "a", 500, null);
        reading.Restore(900, null);
        Assert.Equal(500, reading.Tokens);
        reading.Reset(preserveReading: true);
        Assert.Equal(500, reading.Tokens);
        Assert.True(reading.BeginStep("a", 1));
        Assert.True(reading.ObserveEstimate(Estimate("a", 100, 1)));
        Assert.Equal(100, reading.Tokens);
        reading.Reset();
        Assert.Null(reading.Tokens);
    }

    private static AgentUsageEstimateModel Estimate(string step, long tokens, long sequence) => new(tokens, 120_000, step, sequence);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_input_in_a_new_step_cannot_relabel_the_previous_steps_reading(bool hasBreakdown)
    {
        var reading = new RequestContextReading();
        reading.ObserveUsage(1, "a", 50_000, null);
        reading.BeginStep("b", 2);
        var breakdown = hasBreakdown ? new RuAgent.Protocol.Harness.ContextTokenBreakdown(100, 200, 300) : null;
        reading.ObserveUsage(3, "b", null, breakdown);
        Assert.Equal(hasBreakdown ? 600L : (long?)null, reading.Tokens);
        Assert.Equal(hasBreakdown, reading.IsEstimated);
        Assert.False(reading.HasReportedUsage);
        Assert.False(reading.ObserveEstimate(Estimate("a", 80_000, 100)));
    }
}
