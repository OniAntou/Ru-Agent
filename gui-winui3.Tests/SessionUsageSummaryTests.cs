using System;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionUsageSummaryTests
{
    /// <summary>A row with nothing but the fields these tests are about.</summary>
    private static SessionUsageRow Row(string id, bool terminal, AgentModelUsage? usage) =>
        new(id, terminal, terminal ? "completed" : "running", null, null, usage);

    private static string Outcome(bool terminal, string status) =>
        new SessionUsageRow("r", terminal, status, null, null, null).Outcome;

    [Fact]
    public void Totals_sum_input_and_output_but_peak_the_largest_step()
    {
        // Two steps of one conversation: their input is billed twice, but the window
        // only ever held the larger of the two, so summing them would invent a window
        // nobody had.
        var rows = new[]
        {
            Row("run_1", true, new AgentModelUsage(100, 20, 120, null, null)),
            Row("run_2", true, new AgentModelUsage(900, 50, 950, null, null)),
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
            Row("run_1", true, new AgentModelUsage(10, 1, 11, null, null)),
            Row("run_2", true, null),
            Row("run_3", false, null),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        Assert.Equal(3, summary.Runs);
        Assert.Equal(1, summary.ReportedRuns);
        Assert.Equal(10, summary.TotalInput);
        Assert.Equal(11, summary.PeakTotal);
    }

    [Fact]
    public void Cache_reads_and_writes_stay_separate_figures()
    {
        // Adding them into one "cache" number is what hid the difference between a
        // session that re-read a warm cache and one that paid for fresh input, so the
        // split has to be able to tell them apart.
        var rows = new[]
        {
            Row("run_1", true, new AgentModelUsage(10, 2, 42, 30, 4)),
            Row("run_2", true, new AgentModelUsage(5, 1, 26, 10, 0)),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        Assert.True(summary.HasCache);
        Assert.Equal(40, summary.CacheRead);
        Assert.Equal(4, summary.CacheWrite);
        Assert.Equal(44, summary.CacheRead + summary.CacheWrite);
        // 40 read + 4 write are different bars on the same scale, not one lumped bar.
        Assert.NotEqual(summary.CacheReadShare, summary.CacheWriteShare, 3);
        // The provider's own total wins over the parts, exactly as the meter reads it.
        Assert.Equal(42, summary.PeakTotal);
        Assert.DoesNotContain("cache", summary.TotalsText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_session_that_never_reported_says_so_instead_of_showing_zeros()
    {
        var summary = SessionUsagePolicy.Summarize(new[] { Row("run_1", true, null) });

        Assert.False(summary.HasAny);
        Assert.Equal("No provider report yet.", summary.TotalsText);
        Assert.Equal(string.Empty, summary.CacheText);

        var empty = SessionUsagePolicy.Summarize(Array.Empty<SessionUsageRow>());
        Assert.Equal("No runs yet.", empty.TotalsText);
    }

    [Fact]
    public void Billed_tokens_are_input_plus_output_and_the_split_is_a_share_of_measured()
    {
        var rows = new[] { Row("run_1", true, new AgentModelUsage(60, 20, 90, 20, null)) };

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
    public void A_run_can_state_its_share_of_what_the_session_was_billed()
    {
        var rows = new[]
        {
            Row("run_1", true, new AgentModelUsage(700, 100, 800, null, null)),
            Row("run_2", true, new AgentModelUsage(100, 100, 200, null, null)),
        };

        var summary = SessionUsagePolicy.Summarize(rows);

        // Billed is 800 + 200 = 1000, so the two runs are 80% and 20% of it.
        Assert.Equal(1000, summary.BilledTokens);
        Assert.Equal(80.0, summary.ShareOfBilled(800), 3);
        Assert.Equal(20.0, summary.ShareOfBilled(200), 3);

        // Nothing to be a share of, so no share rather than a full one.
        Assert.Equal(0.0, SessionUsagePolicy.Summarize(Array.Empty<SessionUsageRow>()).ShareOfBilled(0), 3);
        Assert.Equal(0.0, summary.ShareOfBilled(0), 3);
    }

    [Fact]
    public void A_session_that_measured_nothing_has_no_shares()
    {
        var summary = SessionUsagePolicy.Summarize(new[] { Row("run_1", true, null) });

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
    public void A_row_names_the_hosts_own_outcome_rather_than_guessing_why_it_is_quiet()
    {
        // The old wording said "no provider report" for every run that reported nothing,
        // which is true of a failed run, an interrupted one and a successful one alike.
        //
        // The spellings below are the host's, not ours. A first pass of this test used
        // "succeeded", because that is the word the panel had always shown, and every
        // run then fell through the unknown-status fallback: the live panel said
        // "ended" about a run that had plainly completed. The list is pinned so a
        // renamed wire status surfaces here rather than on screen.
        Assert.Equal("completed", Outcome(true, "completed"));
        Assert.Equal("failed", Outcome(true, "failed"));
        Assert.Equal("interrupted", Outcome(true, "interrupted"));
        Assert.Equal("cancelled", Outcome(true, "cancelled"));
        Assert.Equal("created", Outcome(false, "created"));
        Assert.Equal("running", Outcome(false, "running"));
        Assert.Equal("waiting for approval", Outcome(false, "waiting_approval"));
        Assert.Equal("waiting for input", Outcome(false, "waiting_input"));
        Assert.Equal("waiting for trust", Outcome(false, "waiting_workspace_trust"));
        Assert.Equal("waiting for you", Outcome(false, "waiting_user"));
        Assert.Equal("reading history", Outcome(false, "waiting_history"));
        Assert.Equal("reading history", Outcome(false, "waiting_history_final"));
        Assert.Equal("needs reconciliation", Outcome(false, "reconciliation_required"));
        // An unrecognised status still has to name something.
        Assert.Equal("ended", Outcome(true, "something-new"));
        Assert.Equal("in progress", Outcome(false, "something-new"));

        Assert.True(new SessionUsageRow("r", true, "failed", null, null, null).DidNotSucceed);
        Assert.True(new SessionUsageRow("r", true, "cancelled", null, null, null).DidNotSucceed);
        Assert.False(new SessionUsageRow("r", true, "completed", null, null, null).DidNotSucceed);
        // A live run has not failed; it has not finished.
        Assert.False(new SessionUsageRow("r", false, "running", null, null, null).DidNotSucceed);
    }

    [Fact]
    public void A_run_waiting_on_someone_is_marked_as_waiting_not_as_working()
    {
        Assert.True(new SessionUsageRow("r", false, "waiting_approval", null, null, null).IsWaiting);
        Assert.True(new SessionUsageRow("r", false, "waiting_input", null, null, null).IsWaiting);
        Assert.True(new SessionUsageRow("r", false, "waiting_user", null, null, null).IsWaiting);
        Assert.False(new SessionUsageRow("r", false, "running", null, null, null).IsWaiting);
        Assert.False(new SessionUsageRow("r", true, "completed", null, null, null).IsWaiting);
    }

    [Fact]
    public void A_run_measures_its_duration_only_when_both_ends_are_known()
    {
        var start = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(42);

        Assert.Equal(TimeSpan.FromSeconds(42),
            new SessionUsageRow("r", true, "completed", start, end, null).Duration);
        // A live run has no end yet, and an unreported duration is not zero seconds.
        Assert.Null(new SessionUsageRow("r", true, "completed", start, null, null).Duration);
        Assert.Null(new SessionUsageRow("r", false, "running", start, end, null).Duration);
        Assert.Null(new SessionUsageRow("r", true, "completed", null, end, null).Duration);
        // An end before the start is a clock problem, not a negative duration.
        Assert.Null(new SessionUsageRow("r", true, "completed", end, start, null).Duration);
    }

    [Fact]
    public void A_row_line_carries_the_cache_split_the_session_total_would_hide()
    {
        Assert.Equal(
            "in 60 · out 20 · cache read 30 · write 4",
            new SessionUsageRow("r", true, "completed", null, null,
                new AgentModelUsage(60, 20, 114, 30, 4)).Detail);

        // No cache traffic, so no cache clause: an empty clause is noise. A provider
        // that reports a hard zero and one that omits the field both mean "no cache
        // traffic", and the row line does not need to tell those apart — the split
        // section above it is where a zero and a gap are told apart.
        Assert.Equal(
            "in 100 · out 20",
            new SessionUsageRow("r", true, "completed", null, null,
                new AgentModelUsage(100, 20, 120, 0, 0)).Detail);
        Assert.Equal(
            "in 100 · out 20",
            new SessionUsageRow("r", true, "completed", null, null,
                new AgentModelUsage(100, 20, 120, null, null)).Detail);
        // A write on its own is still worth naming.
        Assert.Equal(
            "in 100 · out 20 · write 4",
            new SessionUsageRow("r", true, "completed", null, null,
                new AgentModelUsage(100, 20, 120, null, 4)).Detail);

        // No bare total at the end: an unlabelled number reads as a fourth quantity
        // rather than as the sum of the two beside it, and that sum is already on
        // screen twice — as the billed figure, and as this run's share of it.
        Assert.DoesNotContain(
            new SessionUsageRow("r", true, "completed", null, null,
                new AgentModelUsage(100, 20, 120, null, null)).Detail,
            "120", StringComparison.Ordinal);

        Assert.Equal("completed", new SessionUsageRow("r", true, "completed", null, null, null).Detail);
        Assert.Equal("running", new SessionUsageRow("r", false, "running", null, null, null).Detail);
    }

    [Fact]
    public void A_duration_is_written_at_the_precision_the_number_carries()
    {
        Assert.Equal(string.Empty, SessionTimeFormatter.FormatDuration(null));
        Assert.Equal("0s", SessionTimeFormatter.FormatDuration(TimeSpan.Zero));
        Assert.Equal("42s", SessionTimeFormatter.FormatDuration(TimeSpan.FromSeconds(42)));
        Assert.Equal("59s", SessionTimeFormatter.FormatDuration(TimeSpan.FromSeconds(59.9)));
        Assert.Equal("1m", SessionTimeFormatter.FormatDuration(TimeSpan.FromMinutes(1)));
        Assert.Equal("4m", SessionTimeFormatter.FormatDuration(TimeSpan.FromMinutes(4.9)));
        Assert.Equal("1h 0m", SessionTimeFormatter.FormatDuration(TimeSpan.FromHours(1)));
        Assert.Equal("2h 5m", SessionTimeFormatter.FormatDuration(TimeSpan.FromMinutes(125)));
        // A clock that went backwards is not a negative duration to render.
        Assert.Equal(string.Empty, SessionTimeFormatter.FormatDuration(TimeSpan.FromSeconds(-5)));
    }
}
