using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiUsageTrackerTests
{
    [Fact]
    public void Apply_UsesStepDeltasAgainstTheCumulativeEnvelope()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000 };

        tracker.Apply(inputTokens: 400, outputTokens: 100);
        Assert.Equal(40, tracker.ContextPercent);

        // The second step's envelope is cumulative: 700 total input, 300 total output.
        // Its input is 300, so context occupancy stays at 40%; output tokens affect
        // cost but do not represent retained prompt occupancy.
        tracker.Apply(inputTokens: 700, outputTokens: 300);
        Assert.Equal(40, tracker.ContextPercent);
    }

    [Fact]
    public void BeginTurn_ResetsTheMeterButKeepsTheSessionCost()
    {
        var tracker = new TuiUsageTracker
        {
            ContextLimit = 10_000,
            InputRate = 1,
            OutputRate = 2,
        };

        tracker.Apply(1000, 500);
        var costAfterFirstTurn = tracker.SessionCost;
        Assert.True(costAfterFirstTurn > 0);
        Assert.Equal(10, tracker.ContextPercent);

        tracker.BeginTurn();
        Assert.Null(tracker.ContextPercent);

        // A new run restarts its counters; the reset baseline must not read as negative.
        tracker.Apply(100, 50);
        Assert.Equal(1, tracker.ContextPercent);
        Assert.True(tracker.SessionCost > costAfterFirstTurn);
    }

    [Fact]
    public void Apply_PricesEveryStepAtThePublishedRates()
    {
        var tracker = new TuiUsageTracker { InputRate = 3, OutputRate = 15 };

        tracker.Apply(1_000_000, 1_000_000);

        Assert.True(tracker.CostKnown);
        Assert.Equal(18, tracker.SessionCost, precision: 6);
    }

    [Fact]
    public void Apply_WithoutRatesLeavesTheCostUnpublished()
    {
        var tracker = new TuiUsageTracker();

        tracker.Apply(1000, 1000);

        Assert.False(tracker.CostKnown);
        Assert.Equal(0, tracker.SessionCost);
        Assert.Null(tracker.ContextPercent);
    }

    [Fact]
    public void Apply_WithoutContextLimitLeavesThePercentOff()
    {
        var tracker = new TuiUsageTracker { InputRate = 1, OutputRate = 1 };

        tracker.Apply(500, 500);

        Assert.Null(tracker.ContextPercent);
        Assert.True(tracker.CostKnown);
    }

    [Fact]
    public void Context_reading_uses_input_only_and_resets_at_compaction()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000 };
        tracker.Apply(500, 800);
        Assert.Equal(50, tracker.ContextPercent);

        tracker.ResetContextReading();
        Assert.Null(tracker.ContextPercent);
        tracker.Apply(700, 900);
        Assert.Equal(20, tracker.ContextPercent);
    }

    [Fact]
    public void Output_only_usage_does_not_create_context_occupancy()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 1000 };
        tracker.Apply(null, 400);

        Assert.Null(tracker.ContextPercent);
    }
}
