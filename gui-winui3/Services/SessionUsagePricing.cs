using System;
using System.Collections.Generic;
using System.Globalization;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// What one run cost, given the prices the catalog currently publishes.
/// </summary>
/// <remarks>
/// A token count only becomes money next to a rate, and the two come from different
/// places: the counts are durable in the run's own report, while the rate lives in
/// whatever catalog happens to be loaded. So a run is priced only when both are
/// present, and the panel says how many it could not price rather than quietly
/// dropping them from a total.
/// </remarks>
public sealed record SessionRunPrice(
    string Model,
    decimal? Dollars,
    bool ModelKnown,
    bool RateKnown)
{
    /// <summary>Whether this run contributes a figure to any total.</summary>
    public bool IsPriced => Dollars is not null;

    /// <summary>Why the run carries no figure, in the fewest words that fit.</summary>
    public string Blocker => (ModelKnown, RateKnown) switch
    {
        (false, _) => "no model",
        (_, false) => "no rate",
        _ => string.Empty,
    };
}

/// <summary>
/// Prices a session's runs against the catalog's published rates.
/// </summary>
/// <remarks>
/// Rates are US dollars per million tokens, so a part's cost is its tokens divided by
/// a million and multiplied by its rate. A part the provider publishes no rate for is
/// excluded from the total rather than charged at zero — charging nothing for a cache
/// read is a claim about the price, and the catalog has not made it.
///
/// Nothing here is a fallback. A run whose model the catalog does not list keeps no
/// figure, and the caller is expected to surface the count of those rather than
/// present a total as if it covered the whole session.
/// </remarks>
public static class SessionUsagePricing
{
    private const decimal TokensPerUnit = 1_000_000m;

    /// <summary>
    /// Every rate the catalog publishes, keyed by model id.
    /// </summary>
    /// <remarks>
    /// Only successful, non-stale catalogs are read. A catalog that failed or went
    /// stale is a catalog that may no longer describe the provider, and pricing a
    /// session from it would attribute yesterday's rates to today's runs. A model with
    /// no published rate is simply absent, which is different from one priced at zero.
    /// </remarks>
    public static IReadOnlyDictionary<string, ProviderCatalogCost> RatesFrom(
        ProviderCatalogState? catalog)
    {
        var rates = new Dictionary<string, ProviderCatalogCost>(StringComparer.OrdinalIgnoreCase);
        if (catalog is null || !catalog.IsSuccess || catalog.Stale)
        {
            return rates;
        }

        foreach (var model in catalog.Catalog)
        {
            if (!string.IsNullOrWhiteSpace(model.Id) && model.Cost is { } cost)
            {
                rates.TryAdd(model.Id, cost);
            }
        }

        return rates;
    }

    /// <summary>
    /// Prices every run that can be priced, keyed by run id. Runs absent from the
    /// result are exactly the ones the panel must count as unpriced.
    /// </summary>
    public static IReadOnlyDictionary<string, SessionRunPrice> PriceRuns(
        IEnumerable<SessionUsageRow> rows,
        IReadOnlyDictionary<string, ProviderCatalogCost> rates)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rates);

        var prices = new Dictionary<string, SessionRunPrice>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (prices.ContainsKey(row.RunId))
            {
                continue;
            }

            string? model = row.ModelId;
            if (string.IsNullOrWhiteSpace(model))
            {
                prices[row.RunId] = new SessionRunPrice(string.Empty, null, ModelKnown: false, RateKnown: false);
                continue;
            }

            model = model.Trim();
            if (!rates.TryGetValue(model, out var rate))
            {
                prices[row.RunId] = new SessionRunPrice(model, null, ModelKnown: true, RateKnown: false);
                continue;
            }

            prices[row.RunId] = new SessionRunPrice(
                model,
                Cost(row, rate),
                ModelKnown: true,
                RateKnown: true);
        }

        return prices;
    }

    /// <summary>
    /// One run's cost, or null when a part of it cannot be priced.
    /// </summary>
    /// <remarks>
    /// A run with no report costs nothing and is not an error, so it prices to zero.
    /// A run whose cache traffic is measured but whose rate for it is unpublished does
    /// not: the total would then be missing an amount the session really spent, and a
    /// figure that quietly excludes a part cannot be called the cost of the run.
    ///
    /// Except on a model the provider prices at zero for both input and output. That
    /// is not a missing rate, it is a statement that the model is free, and every
    /// part of a free model is free however its cache is billed. Refusing to price one
    /// would report a genuinely free session as one whose cost nobody knows.
    /// </remarks>
    private static decimal? Cost(SessionUsageRow row, ProviderCatalogCost rate)
    {
        var usage = row.Usage;
        if (usage is null || !row.HasUsage)
        {
            return 0m;
        }

        if (rate.Input == 0 && rate.Output == 0)
        {
            return 0m;
        }

        // Older reports do not identify whether cached tokens overlap input.
        // Keep them readable, but do not publish a price based on guessed buckets.
        if ((usage.InputTokensIncludeCache is null
                && (usage.CacheReadInputTokens > 0 || usage.CacheWriteInputTokens > 0))
            || (usage.InputTokensIncludeCache == true && usage.CacheReadInputTokens is null)
            || (usage.PeakStepTokens is not null && (usage.InputTokens is null || usage.OutputTokens is null
                || usage.CacheReadInputTokens is null || usage.CacheWriteInputTokens is null)))
            return null;

        var total = 0m;
        if (!Part(usage.UncachedInputTokens, rate.Input, ref total)
            || !Part(usage.OutputTokens, rate.Output, ref total)
            || !Part(usage.CacheReadInputTokens, rate.CacheRead, ref total)
            || !Part(usage.CacheWriteInputTokens, rate.CacheWrite, ref total))
        {
            return null;
        }

        return total;
    }

    /// <summary>
    /// Adds one part's cost. A part nobody measured is free; a part that was measured
    /// but has no published rate cannot be priced at all.
    /// </summary>
    private static bool Part(int? tokens, double? rate, ref decimal total)
    {
        if (tokens is not { } count || count == 0)
        {
            return true;
        }

        if (rate is not { } published)
        {
            return false;
        }

        total += (decimal)count / TokensPerUnit * (decimal)published;
        return true;
    }

    /// <summary>
    /// Sums the priced runs. Null when there is nothing priced, so that "nothing to
    /// add up" never renders as a total of zero.
    /// </summary>
    public static decimal? Total(IEnumerable<SessionRunPrice> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);

        decimal total = 0m;
        var any = false;
        foreach (var price in prices)
        {
            if (price.Dollars is { } dollars)
            {
                total += dollars;
                any = true;
            }
        }

        return any ? total : null;
    }

    /// <summary>
    /// A money figure, at the precision a session's size can actually justify.
    /// </summary>
    /// <remarks>
    /// Under a cent, more than two decimals says more than the number knows. Past that
    /// it drops to cents, and past a dollar it shows whole dollars, because at that
    /// scale the cents are not what the reader came for.
    /// </remarks>
    public static string FormatDollars(decimal? dollars)
    {
        if (dollars is not { } value)
        {
            return "—";
        }

        if (value < 0.01m)
        {
            return "$" + value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        if (value < 1m)
        {
            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return "$" + value.ToString("0", CultureInfo.InvariantCulture);
    }
}
