using System;
using System.Collections.Generic;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// One run of the session, oldest first, and the provider report it produced.
/// </summary>
/// <param name="RunId">The run this row stands for.</param>
/// <param name="IsTerminal">Whether the run has ended; a live run has no durable report yet.</param>
/// <param name="Status">The host's own status for the run, verbatim.</param>
/// <param name="CreatedAt">When the run started, or null when the host did not say.</param>
/// <param name="TerminalAt">When the run ended, or null while it is still live.</param>
/// <param name="Usage">The provider's counts, or null when the run never reported.</param>
public sealed record SessionUsageRow(
    string RunId,
    bool IsTerminal,
    string Status,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? TerminalAt,
    AgentModelUsage? Usage)
{
    /// <summary>Whether the provider reported anything for this run.</summary>
    public bool HasUsage => Usage?.HasAny == true;

    /// <summary>
    /// The model this run billed, as the adapter sent it, or null when the run predates
    /// the field or named no model.
    /// </summary>
    public string? ModelId => Usage?.Model;

    /// <summary>
    /// How long the run took, when the host reported both ends of it.
    /// </summary>
    /// <remarks>
    /// A run that has not ended, or one whose times the host did not send, has no
    /// duration rather than a zero - a zero would be a run that finished instantly.
    /// </remarks>
    public TimeSpan? Duration => IsTerminal && CreatedAt is { } start && TerminalAt is { } end && end >= start
        ? end - start
        : null;

    /// <summary>
    /// What happened to the run, in the words the host used. A run that reported no
    /// usage has usually not failed at all, and saying only "no provider report"
    /// reads every quiet run as a broken one.
    /// </summary>
    /// <remarks>
    /// The spellings are the host's own, not ours: a successful run is
    /// <c>completed</c>, not <c>succeeded</c>. Inventing a friendlier word here meant
    /// every run fell through to the unknown-status fallback and the panel said
    /// "ended" about a run that had plainly completed.
    /// </remarks>
    public string Outcome => Status switch
    {
        "completed" => "completed",
        "failed" => "failed",
        "interrupted" => "interrupted",
        "cancelled" => "cancelled",
        "running" => "running",
        "created" => "created",
        "waiting_workspace_trust" => "waiting for trust",
        "waiting_approval" => "waiting for approval",
        "waiting_input" => "waiting for input",
        "waiting_user" => "waiting for you",
        "waiting_history" => "reading history",
        "waiting_history_user" => "waiting on you",
        "waiting_history_final" => "reading history",
        "reconciliation_required" => "needs reconciliation",
        _ => IsTerminal ? "ended" : "in progress",
    };

    /// <summary>Whether the run ended without completing.</summary>
    public bool DidNotSucceed => IsTerminal && !string.Equals(Status, "completed", StringComparison.Ordinal);

    /// <summary>Whether the run is parked waiting on a person rather than working.</summary>
    public bool IsWaiting =>
        Status.StartsWith("waiting", StringComparison.Ordinal);

    /// <summary>Cache reads this run was billed for, or null when unreported.</summary>
    public int? CacheReadTokens => Usage?.CacheReadInputTokens;

    /// <summary>Cache writes this run was billed for, or null when unreported.</summary>
    public int? CacheWriteTokens => Usage?.CacheWriteInputTokens;

    /// <summary>
    /// The provider's counts on one line, including the cache split the session
    /// total would otherwise hide.
    /// </summary>
    /// <remarks>
    /// No bare total at the end. One more number with no word in front of it reads as
    /// a fourth quantity rather than as the sum of the two beside it, and the sum is
    /// already on screen twice: as the session's billed figure, and as this run's
    /// share of it.
    /// </remarks>
    public string Detail => HasUsage
        ? $"in {ModelContextSpecs.FormatTokens(Usage!.InputTokens ?? 0)}"
          + $" · out {ModelContextSpecs.FormatTokens(Usage.OutputTokens ?? 0)}"
          + CacheSuffix()
        : Outcome;

    /// <summary>The cache read/write split, or empty when the run reported neither.</summary>
    public string CacheSuffix()
    {
        if (!HasUsage)
        {
            return string.Empty;
        }

        int read = CacheReadTokens ?? 0;
        int write = CacheWriteTokens ?? 0;
        if (read == 0 && write == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        if (read > 0)
        {
            parts.Add($"cache read {ModelContextSpecs.FormatTokens(read)}");
        }

        if (write > 0)
        {
            parts.Add($"write {ModelContextSpecs.FormatTokens(write)}");
        }

        return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
    }
}

/// <summary>
/// Totals over the runs that reported.
///
/// Input and output are sums, because every step is billed on its own. The peak is
/// the largest single report, because a context window holds one conversation at a
/// time: adding steps together would invent a window nobody ever had.
/// </summary>
public sealed record SessionUsageSummary(
    int Runs,
    int ReportedRuns,
    int TotalInput,
    int TotalOutput,
    int PeakTotal,
    int CacheRead,
    int CacheWrite)
{
    /// <summary>Whether any run reported, i.e. whether the totals mean anything.</summary>
    public bool HasAny => ReportedRuns > 0;

    /// <summary>Whether the provider reported any cache traffic.</summary>
    public bool HasCache => CacheRead > 0 || CacheWrite > 0;

    /// <summary>
    /// Input and output added up: what the session was billed for on every step. Cache
    /// traffic is excluded because a cache read is a discount on an input that was
    /// already counted once.
    /// </summary>
    public int BilledTokens => TotalInput + TotalOutput;

    /// <summary>Share of the measured tokens that arrived as input, in percent.</summary>
    public double InputShare => Share(TotalInput);

    /// <summary>Share of the measured tokens that came back as output, in percent.</summary>
    public double OutputShare => Share(TotalOutput);

    /// <summary>Share of the measured tokens that were cache traffic, in percent.</summary>
    public double CacheShare => Share(CacheRead + CacheWrite);

    /// <summary>Share of the measured tokens that were cache reads, in percent.</summary>
    public double CacheReadShare => Share(CacheRead);

    /// <summary>Share of the measured tokens that were cache writes, in percent.</summary>
    public double CacheWriteShare => Share(CacheWrite);

    private int MeasuredTotal => TotalInput + TotalOutput + CacheRead + CacheWrite;

    private double Share(int part) => MeasuredTotal > 0 ? part * 100.0 / MeasuredTotal : 0.0;

    /// <summary>The totals line: coverage, then the three figures that add up.</summary>
    public string TotalsText => HasAny
        ? $"{ReportedRuns} of {Runs} runs reported · input {ModelContextSpecs.FormatTokens(TotalInput)}"
          + $" · output {ModelContextSpecs.FormatTokens(TotalOutput)}"
          + $" · peak {ModelContextSpecs.FormatTokens(PeakTotal)}"
        : Runs == 0
            ? "No runs yet."
            : "No provider report yet.";

    /// <summary>The cache line, empty when the provider never reported cache traffic.</summary>
    public string CacheText => HasCache
        ? $"cache read {ModelContextSpecs.FormatTokens(CacheRead)}"
          + $" · write {ModelContextSpecs.FormatTokens(CacheWrite)}"
        : string.Empty;

    /// <summary>Every reported run's share of the billed total, in percent.</summary>
    public double ShareOfBilled(int value) =>
        BilledTokens > 0 && value > 0 ? Math.Clamp(value * 100.0 / BilledTokens, 0.0, 100.0) : 0.0;
}

/// <summary>
/// Turns the session's runs into the panel's totals, and states how many runs are
/// worth asking about.
/// </summary>
public static class SessionUsagePolicy
{
    /// <summary>
    /// How many of the newest runs the panel walks. Every run costs two reads — its
    /// events and the usage payload — so the panel reads a bounded tail rather than
    /// the whole history, and says how many runs it covers.
    /// </summary>
    public const int MaxReportedRuns = 8;

    /// <summary>
    /// One run's size as a share of the session's largest single report, in percent.
    /// Steps are compared against each other, never against a model window: the panel
    /// does not know which model each run used, and a share of a limit it guessed
    /// would be a number nobody could act on.
    /// </summary>
    public static double ShareOfPeak(int value, int peak)
    {
        if (peak <= 0 || value <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(value * 100.0 / peak, 0.0, 100.0);
    }

    public static SessionUsageSummary Summarize(IEnumerable<SessionUsageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int runs = 0;
        int reported = 0;
        int input = 0;
        int output = 0;
        int peak = 0;
        int cacheRead = 0;
        int cacheWrite = 0;

        foreach (var row in rows)
        {
            runs++;
            if (!row.HasUsage)
            {
                continue;
            }

            reported++;
            var usage = row.Usage!;
            input += usage.InputTokens ?? 0;
            output += usage.OutputTokens ?? 0;
            cacheRead += usage.CacheReadInputTokens ?? 0;
            cacheWrite += usage.CacheWriteInputTokens ?? 0;

            int total = usage.ResolvedTotal;
            if (total > peak)
            {
                peak = total;
            }
        }

        return new SessionUsageSummary(runs, reported, input, output, peak, cacheRead, cacheWrite);
    }
}
