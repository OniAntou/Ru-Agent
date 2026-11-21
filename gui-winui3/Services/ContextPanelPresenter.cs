using System.Globalization;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

internal sealed record ContextPanelReading(string System, string Tools, string Messages,
    long SystemWeight, long ToolWeight, long MessageWeight, string Note, bool HasBreakdown, bool NextRequestOnly);

internal static class ContextPanelPresenter
{
    /// <summary>
    /// The Messages row's tooltip. The allowance is read from the shared constant so the
    /// number the user reads is the number the estimate charges.
    /// </summary>
    internal static string ImagesTooltip =>
        "Estimates include the current draft. Images use a fixed "
        + ContextTokenBreakdown.ImageTokenAllowance.ToString("N0", CultureInfo.InvariantCulture)
        + "-token allowance per image, not provider image tokenization.";

    internal static ContextPanelReading Project(
        ContextTokenBreakdown? breakdown,
        int draftTokens,
        int visibleEstimate,
        bool knownHistory,
        bool requestFailed = false,
        bool hasReportedUsage = true,
        int baselineTokens = 0)
    {
        static string Format(int? tokens) => tokens is { } count ? "~" + ModelContextSpecs.FormatTokens(count) : "—";
        // Before a first request the visible estimate includes the remembered fixed prompt.
        int messages = (int)Math.Min(int.MaxValue, (long)(breakdown?.Messages ?? Math.Max(0, visibleEstimate - Math.Max(0, baselineTokens)))
            + (breakdown is null ? 0 : Math.Max(0, draftTokens)));
        bool nextRequestOnly = requestFailed && !hasReportedUsage;
        return new(Format(breakdown?.SystemPrompt), Format(breakdown?.ToolDefinitions), Format(messages),
            breakdown?.SystemPrompt ?? 0, breakdown?.ToolDefinitions ?? 0, messages,
            !knownHistory ? "Retained context unknown · draft estimate only"
                : nextRequestOnly ? "Last request failed · no provider usage reported; values are estimated for the next try"
                : breakdown is null && baselineTokens > 0
                    ? $"Before the first request · system prompt and tools ~{ModelContextSpecs.FormatTokens(baselineTokens)} from this model's last request"
                : breakdown is null ? "Breakdown available after the next request"
                : "Estimated · draft and image allowance included",
            breakdown is not null,
            nextRequestOnly);
    }
}
