using RuAgent.Cli;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiContextWindowRegressionTests
{
    [Fact]
    public void Context_percentage_uses_the_published_window_not_the_smaller_prompt_budget()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 128_000 };
        tracker.ObserveEstimate(new(64_000, 120_000, "a", 1));
        Assert.Equal(50d, tracker.ContextPercent);
        Assert.Equal(128_000, tracker.ContextLimit);
    }

    [Fact]
    public void Provider_input_corrects_an_overestimate_instead_of_freezing_the_peak()
    {
        var tracker = new TuiUsageTracker { ContextLimit = 100_000 };
        tracker.ObserveEstimate(new(80_000, 100_000, "a", 1));
        tracker.ObserveUsage(2, "a", 50_000, null);
        Assert.Equal(50d, tracker.ContextPercent);
        tracker.BeginStep("b", 3);
        tracker.ObserveEstimate(new(55_000, 100_000, "b", 2));
        tracker.ObserveUsage(4, "b", 52_000, null);
        Assert.Equal(52d, tracker.ContextPercent);
    }

    [Fact]
    public async Task A_supported_messages_run_keeps_its_pinned_context_window()
    {
        var tracker = new TuiUsageTracker();
        var metadata = new ChatModelUsage((_, _, _) => Task.FromResult<CatalogModelDetails?>(new(200_000, 1, 2, null, null)));
        await metadata.RefreshAsync(tracker, new("Anthropic", "model", true, 100_000, "anthropic_messages"), true, default);
        Assert.Equal(100_000, tracker.ContextLimit);
    }
}
