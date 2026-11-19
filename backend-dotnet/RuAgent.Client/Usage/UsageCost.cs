namespace RuAgent.Client.Usage;

/// <summary>
/// Token counts of one provider usage report, or of a run's reports added up
/// (<see cref="IsAggregate"/>). Null means the provider did not report that count.
/// </summary>
public readonly record struct UsageCounts(
    long? Input,
    long? Output,
    long? CacheRead,
    long? CacheWrite,
    bool? InputIncludesCache,
    bool IsAggregate = false);

/// <summary>US dollars per million tokens, as the provider catalog publishes them; null when unpublished.</summary>
public readonly record struct UsageRates(double? Input, double? Output, double? CacheRead, double? CacheWrite);

/// <summary>
/// What a run's tokens cost at the catalog's current rates. The desktop session usage panel
/// and the terminal's <c>/status usage</c> both price through here, so the two clients
/// never show different figures for the same run.
/// </summary>
/// <remarks>
/// A cost is published only when every billed part can be priced:
/// <list type="bullet">
/// <item>Input and output must be reported; every request has both, so a missing one is
/// not zero. Cache counts a provider does not report are cache it did not use.</item>
/// <item>A measured part with no published rate makes the whole cost unknown: a total that
/// quietly leaves it out is not the cost of the run.</item>
/// <item>A model priced at zero for input and output is free, cache traffic included;
/// refusing to price it would call a free session one whose cost nobody knows.</item>
/// <item>A report that does not say whether cached tokens overlap input, but has cache
/// traffic, cannot be split into parts, so it has no cost. Neither does an aggregate with
/// a count missing, since a missing count there may hide a measured step.</item>
/// </list>
/// Nothing here is an invoice: the rates are whatever the catalog publishes now.
/// </remarks>
public static class UsageCost
{
    private const decimal TokensPerUnit = 1_000_000m;
    private const double MaximumRate = 1_000_000_000d;

    /// <summary>Input tokens not served from or written to the cache, or null when unknown.</summary>
    public static long? UncachedInput(UsageCounts counts) => counts.Input is { } input
        ? counts.InputIncludesCache == true
            ? counts.CacheRead is { } read ? Math.Max(0, input - read - (counts.CacheWrite ?? 0)) : null
            : input
        : null;

    public static decimal? Price(UsageCounts counts, UsageRates rates)
    {
        if (rates.Input == 0 && rates.Output == 0)
        {
            return 0m;
        }

        var cacheTraffic = counts.CacheRead > 0 || counts.CacheWrite > 0;
        if (counts.Input is null || counts.Output is null
            || (counts.InputIncludesCache is null && cacheTraffic)
            || (counts.InputIncludesCache == true && counts.CacheRead is null)
            || (counts.IsAggregate && (counts.CacheRead is null || counts.CacheWrite is null)))
        {
            return null;
        }

        var total = 0m;
        return Part(UncachedInput(counts), rates.Input, ref total)
            && Part(counts.Output, rates.Output, ref total)
            && Part(counts.CacheRead, rates.CacheRead, ref total)
            && Part(counts.CacheWrite, rates.CacheWrite, ref total)
                ? total
                : null;
    }

    private static bool Part(long? tokens, double? rate, ref decimal total)
    {
        if (tokens is not { } count || count == 0)
        {
            return true;
        }

        if (rate is not { } published || !double.IsFinite(published) || published is < 0 or > MaximumRate)
        {
            return false;
        }

        total += count / TokensPerUnit * (decimal)published;
        return true;
    }
}
