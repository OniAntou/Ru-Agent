using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

internal sealed record ContextPanelReading(string System, string Tools, string Messages,
    long SystemWeight, long ToolWeight, long MessageWeight, string Note, bool HasBreakdown);

internal static class ContextPanelPresenter
{
    internal static ContextPanelReading Project(ContextTokenBreakdown? breakdown, int draftTokens, int visibleEstimate, bool knownHistory)
    {
        static string Format(int? tokens) => tokens is { } count ? "~" + ModelContextSpecs.FormatTokens(count) : "—";
        int messages = (int)Math.Min(int.MaxValue, (long)(breakdown?.Messages ?? Math.Max(0, visibleEstimate))
            + (breakdown is null ? 0 : Math.Max(0, draftTokens)));
        return new(Format(breakdown?.SystemPrompt), Format(breakdown?.ToolDefinitions), Format(messages),
            breakdown?.SystemPrompt ?? 0, breakdown?.ToolDefinitions ?? 0, messages,
            !knownHistory ? "Retained context unknown · draft estimate only"
                : breakdown is null ? "Breakdown available after the next request"
                : "Estimated · draft and image allowance included", breakdown is not null);
    }
}
