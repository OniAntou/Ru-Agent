using System;
using System.Collections.Generic;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// One run of the session, oldest first, and the provider report it produced.
/// </summary>
/// <param name="RunId">The run this row stands for.</param>
/// <param name="IsTerminal">Whether the run has ended; a live run has no durable report yet.</param>
/// <param name="Usage">The provider's counts, or null when the run never reported.</param>
public sealed record SessionUsageRow(string RunId, bool IsTerminal, AgentModelUsage? Usage)
{
    /// <summary>Whether the provider reported anything for this run.</summary>
    public bool HasUsage => Usage?.HasAny == true;

    /// <summary>One line for the panel: the reported counts, or why there are none.</summary>
    public string Detail => HasUsage
        ? Usage!.DisplayText
        : IsTerminal ? "no provider report" : "still running";
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
