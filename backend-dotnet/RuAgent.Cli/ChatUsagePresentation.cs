using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal static class ChatUsagePresentation
{
    internal static long DraftEstimate(string text, long textAttachmentCharacters, int images) =>
        (long)Math.Ceiling((text.Length + Math.Max(0, textAttachmentCharacters)) / 3.5d)
        + (long)Math.Max(0, images) * ContextTokenBreakdown.ImageTokenAllowance;

    internal static IReadOnlyList<string> Context(ChatUsageReading reading, AgentUsageEstimateModel? prepared,
        long? catalogLimit, long draft, string? selectedModel = null)
    {
        var row = reading.Runs.LastOrDefault();
        var report = row?.LatestContext;
        var breakdown = report?.Context ?? (report is null ? prepared?.ContextTokens : null);
        var reported = report?.Prompt(row?.Run.ProviderProtocol);
        var used = reported ?? (report is null ? prepared?.EstimatedInputTokens : null) ?? breakdown?.Total;
        var limit = row is null ? catalogLimit : row.Run.ContextWindowTokens;
        var lines = new List<string> {
            "Context · latest request", "Session: " + (reading.Session.Length == 0 ? "not started" : reading.Session),
            "Run: " + (row?.Run.RunId ?? "not started"),
            "Model: " + Safe(row?.Run.ProviderModel ?? selectedModel ?? "not set"),
            "Published context window: " + ChatTokenUsage.Number(limit),
            "Output reserve: " + ChatTokenUsage.Number(row?.Run.OutputTokenReserve),
            "Prompt tokens: " + ChatTokenUsage.Number(used) + (used is null ? "" : reported is null ? " (estimated)" : " (provider report)"),
            "Context used: " + (used is { } tokens && limit is > 0 ? (100d * tokens / limit.Value).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : "unknown"),
            "", "Request breakdown (estimated)",
            "System prompt: " + ChatTokenUsage.Number(breakdown?.SystemPrompt),
            "Tool definitions: " + ChatTokenUsage.Number(breakdown?.ToolDefinitions),
            "Messages and attachments: " + ChatTokenUsage.Number(breakdown?.Messages),
            "Request estimate: " + ChatTokenUsage.Number(breakdown?.Total),
            "Unsent draft: ~" + ChatTokenUsage.Number(draft) + " tokens (separate estimate)",
        };
        if (prepared is not null) lines.Add("Prepared prompt budget: " + ChatTokenUsage.Number(prepared.MaxPromptTokens));
        lines.Add(row?.Notice ?? (report is null && prepared is null ? "No current request reading; the next request will provide one." : ""));
        lines.Add("Context is one request, not accumulated billed tokens. Draft uses ~1 token/3.5 characters plus an image allowance.");
        return lines;
    }

    internal static IReadOnlyList<string> Usage(ChatUsageReading reading)
    {
        var rows = reading.Runs;
        var reports = rows.SelectMany(row => row.Reports).ToArray();
        string Metric(Func<ChatTokenUsage, long?> field)
        {
            var values = reports.Select(field).Where(value => value is not null).ToArray();
            var complete = rows.All(row => row.Notice is null) && values.Length == reports.Length;
            return values.Length == 0 ? "unknown" : ChatTokenUsage.Number(values.Sum(value => value!.Value)) + (complete ? "" : " (partial)");
        }
        var prices = rows.Where(row => row.Cost is not null).ToArray();
        var cached = reports.Where(report => report.IncludesCache is not null && report.CacheRead is not null && report.Prompt(null) is > 0).ToArray();
        var hit = cached.Length == 0 ? "unknown" : (100d * cached.Sum(report => report.CacheRead!.Value)
            / cached.Sum(report => report.Prompt(null)!.Value)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            + "%" + (cached.Length < reports.Length ? " (partial)" : "");
        var lines = new List<string> {
            "Session usage · recent runs", "Session: " + (reading.Session.Length == 0 ? "not started" : reading.Session),
            $"Scope: newest {rows.Count} of {reading.ListedRuns} listed runs (maximum {ChatUsageReader.MaxRuns}; not all-time billing)",
            $"Coverage: {rows.Count(row => row.Reports.Count > 0)}/{rows.Count} runs reported · {reports.Length} distinct step reports",
            "Input (uncached): " + Metric(report => report.UncachedInput),
            "Output: " + Metric(report => report.Output),
            "Cache read: " + Metric(report => report.CacheRead),
            "Cache write: " + Metric(report => report.WriteBucket),
            "Cache hit rate (known accounting): " + hit,
            "Billed tokens: " + Metric(report => report.ResolvedTotal),
            "Largest reported step: " + ChatTokenUsage.Number(reports.Select(report => report.ResolvedTotal).Where(value => value is not null).DefaultIfEmpty().Max()),
            "Estimated cost: " + (rows.Count > 0 && prices.Length == rows.Count ? ChatTokenUsage.Dollars(prices.Sum(row => row.Cost!.Value)) : "unknown"),
            $"Priced coverage: {prices.Length}/{rows.Count} runs · priced subtotal {ChatTokenUsage.Dollars(prices.Length == 0 ? null : prices.Sum(row => row.Cost!.Value))}",
            "", "Per run (oldest to newest within this scope)",
        };
        foreach (var row in rows)
        {
            lines.Add(Safe(row.Run.RunId + " · " + row.Run.Status + " · " + row.Run.ProviderBackend + " / " + row.Run.ProviderModel));
            lines.Add(row.Notice is { } notice ? "  Usage unavailable: " + Safe(notice)
                : $"  {row.Reports.Count} steps · billed {ChatTokenUsage.Number(SumComplete(row.Reports.Select(report => report.ResolvedTotal)))} · cost {ChatTokenUsage.Dollars(row.Cost)}");
            if (row.Reports.Count > 0)
                lines.Add($"  Uncached input {ChatTokenUsage.Number(SumComplete(row.Reports.Select(report => report.UncachedInput)))}"
                    + $" · output {ChatTokenUsage.Number(SumComplete(row.Reports.Select(report => report.Output)))}"
                    + $" · cache read {ChatTokenUsage.Number(SumComplete(row.Reports.Select(report => report.CacheRead)))}"
                    + $" · write {ChatTokenUsage.Number(SumComplete(row.Reports.Select(report => report.WriteBucket)))}");
        }
        lines.Add("Missing reports/accounting/rates stay unknown. Cost uses current catalog rates, not an invoice; only this session's listed runs are included.");
        return lines;
    }

    private static long? SumComplete(IEnumerable<long?> counts)
    {
        var values = counts.ToArray();
        return values.Length > 0 && values.All(value => value is not null) ? values.Sum(value => value!.Value) : null;
    }
    private static string Safe(string value) => ChatDisplayText.Sanitize(value, 512);
}
