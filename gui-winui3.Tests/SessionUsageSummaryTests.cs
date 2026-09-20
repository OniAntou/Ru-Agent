using System;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionUsageSummaryTests
{
    [Fact]
    public void Totals_sum_input_and_output_but_peak_the_largest_step()
    {
        // Two steps of one conversation: their input is billed twice, but the window
        // only ever held the larger of the two, so summing them would invent a window
        // nobody had.
        var rows = new[]
        {
            new SessionUsageRow("run_1", true, new AgentModelUsage(100, 20, 120, null, null)),
            new SessionUsageRow("run_2", true, new AgentModelUsage(900, 50, 950, null, null)),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(2, summary.ReportedRuns);
        Assert.Equal(1000, summary.TotalInput);
        Assert.Equal(70, summary.TotalOutput);
        Assert.Equal(950, summary.PeakTotal);
        Assert.Contains("2 of 2 runs reported", summary.TotalsText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_a_report_counts_as_a_run_but_adds_nothing()
    {
        var rows = new[]
        {
            new SessionUsageRow("run_1", true, new AgentModelUsage(10, 1, 11, null, null)),
            new SessionUsageRow("run_2", true, null),
            new SessionUsageRow("run_3", false, null),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        Assert.Equal(3, summary.Runs);
        Assert.Equal(1, summary.ReportedRuns);
        Assert.Equal(10, summary.TotalInput);
        Assert.Equal(11, summary.PeakTotal);
    }

    [Fact]
    public void Cache_traffic_is_summed_and_reported_separately()
    {
        var rows = new[]
        {
            new SessionUsageRow("run_1", true, new AgentModelUsage(10, 2, 42, 30, null)),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        Assert.True(summary.HasCache);
        Assert.Equal(30, summary.CacheRead);
        Assert.Equal(0, summary.CacheWrite);
        // The provider's own total wins over the parts, exactly as the meter reads it.
        Assert.Equal(42, summary.PeakTotal);
        Assert.DoesNotContain("cache", summary.TotalsText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_session_that_never_reported_says_so_instead_of_showing_zeros()
    {
        var summary = SessionUsagePolicy.Summarize(
            new[] { new SessionUsageRow("run_1", true, null) });

        Assert.False(summary.HasAny);
        Assert.Equal("No provider report yet.", summary.TotalsText);
        Assert.Equal(string.Empty, summary.CacheText);

        var empty = SessionUsagePolicy.Summarize(Array.Empty<SessionUsageRow>());
        Assert.Equal("No runs yet.", empty.TotalsText);
    }

    [Fact]
    public void Billed_tokens_are_input_plus_output_and_the_split_is_a_share_of_measured()
    {
        var rows = new[]
        {
            new SessionUsageRow("run_1", true, new AgentModelUsage(60, 20, 90, 20, null)),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        // Cache is measured but not billed twice: a cache read is a discount on an input
        // that was already counted.
        Assert.Equal(80, summary.BilledTokens);

        // 60 input + 20 output + 20 cache read = 100 measured, so the shares add up.
        Assert.Equal(60.0, summary.InputShare, 3);
        Assert.Equal(20.0, summary.OutputShare, 3);
        Assert.Equal(20.0, summary.CacheShare, 3);
        Assert.Equal(100.0, summary.InputShare + summary.OutputShare + summary.CacheShare, 3);
    }

    [Fact]
    public void A_session_that_measured_nothing_has_no_shares()
    {
        var summary = SessionUsagePolicy.Summarize(
            new[] { new SessionUsageRow("run_1", true, null) });

        Assert.Equal(0, summary.BilledTokens);
        Assert.Equal(0.0, summary.InputShare, 3);
        Assert.Equal(0.0, summary.OutputShare, 3);
        Assert.Equal(0.0, summary.CacheShare, 3);
    }

    [Fact]
    public void A_run_is_sized_against_the_largest_step_not_against_a_guessed_window()
    {
        Assert.Equal(50.0, SessionUsagePolicy.ShareOfPeak(500, 1000), 3);
        Assert.Equal(100.0, SessionUsagePolicy.ShareOfPeak(1000, 1000), 3);

        // A peak that is missing, or a step that reported nothing, draws no bar at all
        // rather than a full-width one.
        Assert.Equal(0.0, SessionUsagePolicy.ShareOfPeak(500, 0), 3);
        Assert.Equal(0.0, SessionUsagePolicy.ShareOfPeak(0, 1000), 3);
        Assert.Equal(0.0, SessionUsagePolicy.ShareOfPeak(-5, 1000), 3);
    }

    [Fact]
    public void Resolved_total_prefers_the_provider_total_and_otherwise_adds_the_parts()
    {
        Assert.Equal(120, new AgentModelUsage(100, 20, 120, null, null).ResolvedTotal);
        Assert.Equal(150, new AgentModelUsage(100, 20, null, 30, null).ResolvedTotal);
        // An explicit zero total is not a usable figure, so the parts answer instead.
        Assert.Equal(100, new AgentModelUsage(100, null, 0, null, null).ResolvedTotal);
    }

    [Fact]
    public void A_row_says_why_it_has_no_figures()
    {
        Assert.Equal("still running", new SessionUsageRow("run_1", false, null).Detail);
        Assert.Equal("no provider report", new SessionUsageRow("run_1", true, null).Detail);
        Assert.Equal(
            "5 output",
            new SessionUsageRow("run_1", true, new AgentModelUsage(null, 5, null, null, null)).Detail);
    }
}
