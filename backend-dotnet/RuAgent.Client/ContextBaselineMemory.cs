using System.Collections.Concurrent;

namespace RuAgent.Client;

/// <summary>
/// What a route's fixed prompt cost the last time this process saw it: the system prompt and
/// tool definitions, in provider tokens, and the route's provider-to-heuristic ratio.
/// </summary>
/// <remarks>
/// Before a session's first request nothing is pinned and no request exists, so the meter
/// had only the visible text to count and started near zero although every request opens
/// with the system prompt and tool definitions (about 15k tokens on a typical coding run).
/// The last figure seen for the same route stands in for it, marked as an estimate, until
/// the session's own first request is measured. Nothing is shown for a route never seen.
/// Process-local and bounded; it holds counts only, never prompt text.
/// </remarks>
public static class ContextBaselineMemory
{
    private const int MaximumRoutes = 64;
    private static readonly ConcurrentDictionary<string, (long? Baseline, double? Ratio)> Routes = new(StringComparer.Ordinal);

    /// <summary>The route key a reading and a not-yet-started session share: protocol and model.</summary>
    public static string? RouteKey(string? protocol, string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : (protocol ?? string.Empty) + "|" + model.Trim();

    /// <summary>The route key for a model picked before any run exists, protocol not yet known.</summary>
    public static string? ModelKey(string? model) => RouteKey(null, model);

    public static void RememberRatio(string route, double ratio)
    {
        Update(route, entry => entry with { Ratio = ratio });
    }

    /// <summary>Records the fixed prompt part, given in heuristic tokens, converted by the ratio when known.</summary>
    public static void RememberBaseline(string route, long heuristicTokens, double? ratio)
    {
        if (heuristicTokens <= 0) return;
        var tokens = (long)Math.Round(heuristicTokens * (ratio ?? 1.0));
        Update(route, entry => entry with { Baseline = tokens });
    }

    public static double? RatioFor(string route) =>
        Routes.TryGetValue(route, out var entry) && entry.Ratio is { } ratio ? ratio : Lookup(route)?.Ratio;

    public static long? BaselineFor(string? route) =>
        route is null ? null
            : Routes.TryGetValue(route, out var entry) && entry.Baseline is { } tokens ? tokens
            : Lookup(route)?.Baseline;

    /// <summary>Forgets every route; tests use it to start clean.</summary>
    public static void Clear() => Routes.Clear();

    private static void Update(string route, Func<(long? Baseline, double? Ratio), (long? Baseline, double? Ratio)> change)
    {
        if (Routes.Count >= MaximumRoutes && !Routes.ContainsKey(route)) Routes.Clear();
        Routes.AddOrUpdate(route, _ => change((null, null)), (_, current) => change(current));
        // A session that has not started knows its model but not yet its protocol.
        var model = route[(route.IndexOf('|') + 1)..];
        var modelKey = ModelKey(model);
        if (modelKey is not null && modelKey != route)
            Routes.AddOrUpdate(modelKey, _ => change((null, null)), (_, current) => change(current));
    }

    /// <summary>A protocol-qualified route falls back to what its model alone recorded.</summary>
    private static (long? Baseline, double? Ratio)? Lookup(string route)
    {
        var separator = route.IndexOf('|');
        if (separator <= 0) return null;
        return Routes.TryGetValue(route[separator..], out var entry) ? entry : null;
    }
}
