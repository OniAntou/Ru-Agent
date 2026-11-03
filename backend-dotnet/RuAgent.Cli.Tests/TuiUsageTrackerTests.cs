using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiUsageTrackerTests
{
    [Fact]
    public void Billing_cannot_be_mistaken_for_one_requests_context_even_after_reconnect()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000, InputRate = 3, OutputRate = 15 };
        tracker.ApplyBilling(1_000_000, 1_000_000);
        tracker.ApplyBilling(1_000_000, 1_000_000);
        Assert.Null(tracker.ContextTokens);
        Assert.Null(tracker.ContextPercent);
        Assert.True(tracker.CostKnown);
        Assert.Equal(18, tracker.SessionCost, 6);
        tracker.ObserveUsage(100, "a", 400, null);
        Assert.Equal(40d, tracker.ContextPercent);
    }

    [Fact]
    public void Latest_request_replaces_the_peak_and_keeps_fractional_precision()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 10_000 };
        tracker.BeginStep("a", 1);
        tracker.ObserveEstimate(new(1000, 9000, "a", 1));
        tracker.ObserveUsage(2, "a", 432, null);
        Assert.Equal(4.32, tracker.ContextPercent);
        Assert.False(tracker.ContextIsEstimated);
        tracker.BeginStep("b", 3);
        tracker.ObserveUsage(4, "b", 300, null);
        Assert.Equal(3d, tracker.ContextPercent);
    }

    [Fact]
    public void A_new_run_keeps_the_last_reading_and_session_cost_but_resets_billing_baseline()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 10_000, InputRate = 1, OutputRate = 2 };
        tracker.BindRun("a");
        tracker.ApplyBilling(1000, 500);
        tracker.ObserveUsage(1, "a", 1000, null);
        var previousCost = tracker.SessionCost;
        tracker.BindRun("b");
        Assert.Equal(10d, tracker.ContextPercent);
        tracker.ApplyBilling(100, 50);
        tracker.ObserveUsage(1, "b", 100, null);
        Assert.Equal(1d, tracker.ContextPercent);
        Assert.True(tracker.SessionCost > previousCost);
        var nextCost = tracker.SessionCost;
        tracker.BindRun("b"); // Continue must not price the same cumulative counters twice.
        tracker.ApplyBilling(100, 50);
        Assert.Equal(nextCost, tracker.SessionCost);
    }

    [Fact]
    public void Unknown_capacity_shows_request_count_without_inventing_a_window_from_prompt_budget()
    {
        var tracker = new TuiUsageTracker();
        tracker.ObserveEstimate(new(2000, 8000, "a", 1));
        Assert.Equal(2000, tracker.ContextTokens);
        Assert.True(tracker.ContextIsEstimated);
        Assert.Null(tracker.ContextPercent);
        Assert.False(tracker.CostKnown);
        tracker.ContextLimit = 10_000; // Metadata arriving later recomputes the same reading.
        Assert.Equal(20d, tracker.ContextPercent);
    }

    [Fact]
    public void Compaction_clears_the_old_reading_while_preserving_cost_and_billing_baseline()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000, InputRate = 1, OutputRate = 2 };
        tracker.ApplyBilling(500, 800);
        tracker.ObserveUsage(1, "a", 500, null);
        var cost = tracker.SessionCost;
        Assert.True(tracker.Compact(2));
        Assert.Null(tracker.ContextPercent);
        Assert.False(tracker.ObserveEstimate(new(500, 900, "a", 5)));
        Assert.Equal(cost, tracker.SessionCost);
        tracker.BeginStep("b", 3);
        tracker.ApplyBilling(700, 900);
        tracker.ObserveUsage(4, "b", 200, null);
        Assert.Equal(20d, tracker.ContextPercent);
        Assert.Equal(cost + .0004, tracker.SessionCost, 8);
    }

    [Fact]
    public void Missing_and_zero_input_are_distinct_and_cost_without_rates_remains_unknown()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000 };
        tracker.ApplyBilling(null, 400);
        tracker.ObserveUsage(1, "a", null, null);
        Assert.Null(tracker.ContextPercent);
        Assert.False(tracker.CostKnown);
        tracker.BeginStep("b", 2);
        tracker.ObserveUsage(3, "b", 0, null);
        Assert.Equal(0d, tracker.ContextPercent);
    }
}
