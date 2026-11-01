using System.Globalization;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>One provider report. Missing counts remain unknown; cached input may overlap input.</summary>
internal sealed record ChatTokenUsage(long? Input, long? Output, long? Total, long? CacheRead,
    long? CacheWrite, bool? IncludesCache, string? Model, ContextTokenBreakdown? Context)
{
    internal long? WriteBucket => IncludesCache == true ? CacheWrite ?? 0 : CacheWrite;
    internal long? UncachedInput => IncludesCache == true
        ? Input is { } input && CacheRead is { } read ? Math.Max(0, input - read - (CacheWrite ?? 0)) : null
        : IncludesCache == false || (CacheRead == 0 && CacheWrite == 0) ? Input : null;
    internal long? ResolvedTotal => Total ?? (IncludesCache == true ? Sum(Input, Output)
        : Sum(UncachedInput, Output, CacheRead, WriteBucket));
    internal long? Prompt(string? protocol) => IncludesCache == true ? Input
        : IncludesCache == false ? Sum(Input, CacheRead, WriteBucket)
        : protocol is "chat_completions" or "openai_responses" ? Input
        : protocol == "anthropic_messages" ? Sum(Input, CacheRead, CacheWrite) : null;

    internal static long? Sum(params long?[] values) => values.All(value => value is not null)
        ? values.Sum(value => value!.Value) : null;

    internal static ChatTokenUsage Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            if (root.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw Invalid();
            long? Count(string name)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                if (!value.TryGetInt64(out var count) || count is < 0 or > int.MaxValue) throw Invalid();
                return count;
            }
            bool? inclusive = null;
            if (root.TryGetProperty("input_tokens_include_cache", out var accounting) && accounting.ValueKind != JsonValueKind.Null)
            {
                if (accounting.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                inclusive = accounting.GetBoolean();
            }
            string? model = root.TryGetProperty("model", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 and <= 256 } id && !id.Any(char.IsControl) ? id : null;
            ContextTokenBreakdown? context = null;
            if (root.TryGetProperty("context_tokens", out var breakdown) && breakdown.ValueKind != JsonValueKind.Null
                && !ContextTokenBreakdown.TryRead(breakdown, out context)) throw Invalid();
            var report = new ChatTokenUsage(Count("input_tokens"), Count("output_tokens"), Count("total_tokens"),
                Count("cache_read_input_tokens"), Count("cache_write_input_tokens"), inclusive, model, context);
            if (report.IncludesCache == true && report.Input is { } input
                && (report.CacheRead ?? 0) + (report.CacheWrite ?? 0) > input) throw Invalid();
            if (report.Input is null && report.Output is null && report.Total is null
                && report.CacheRead is null && report.CacheWrite is null) throw Invalid();
            return report;
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
    }

    internal decimal? Price(CatalogModelDetails? rates)
    {
        if (rates is null || UncachedInput is null || Output is null || CacheRead is null || WriteBucket is null) return null;
        decimal cost = 0;
        foreach (var (count, rate) in new[] { (UncachedInput, rates.InputCost), (Output, rates.OutputCost),
                     (CacheRead, rates.CacheReadCost), (WriteBucket, rates.CacheWriteCost) })
        {
            if (count == 0) continue;
            if (rate is not { } published || !double.IsFinite(published) || published is < 0 or > 1_000_000_000) return null;
            cost += count!.Value / 1_000_000m * (decimal)published;
        }
        return cost;
    }

    internal static string Number(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
    internal static string Dollars(decimal? value) => value?.ToString("$0.000000", CultureInfo.InvariantCulture) ?? "unknown";
    private static AgentApiException Invalid() => new("usage_report_invalid", "Provider usage report is invalid or incomplete.");
}
