using System.Text;
using RuAgent.Harness.Core.Reconciliation;

namespace RuAgent.Harness.Core.Context;

public sealed record ContinuationSummary(string Text, int SourceEntryCount);

/// <summary>
/// Deterministic bounded continuation summary for a context that hit a true provider
/// limit. Newest entries are summarized first so the most recent results survive; no
/// entry is ever silently dropped — the header always reports the full source count.
/// </summary>
public static class ContinuationSummaryBuilder
{
    public const int DefaultMaxChars = 4000;
    private const int MaxEntryHeadChars = 400;

    public static ContinuationSummary Build(ModelContextSnapshot snapshot, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 64);

        var builder = new StringBuilder(Math.Min(maxChars, 1024));
        _ = builder
            .Append("continuation-summary entries=")
            .Append(snapshot.Entries.Count)
            .Append(" truncated_for_limit=true");
        var included = 0;
        for (var index = snapshot.Entries.Count - 1; index >= 0; index--)
        {
            var entry = snapshot.Entries[index];
            AppendEntry(builder, entry);
            included++;
            if (builder.Length >= maxChars)
            {
                break;
            }
        }

        if (builder.Length > maxChars)
        {
            builder.Length = maxChars;
        }

        return new ContinuationSummary(builder.ToString(), snapshot.Entries.Count);
    }

    private static void AppendEntry(StringBuilder builder, ModelContextEntry entry)
    {
        builder.Append("\n#seq=").Append(entry.Sequence)
            .Append(" role=").Append(entry.Role)
            .Append(" kind=").Append(entry.Kind)
            .Append(' ');
        var content = entry.Kind switch
        {
            ModelContextEntryKind.Text => entry.Content ?? string.Empty,
            ModelContextEntryKind.AssistantStep => entry.AssistantStep?.Text ?? string.Empty,
            ModelContextEntryKind.ToolResult => entry.Content ?? string.Empty,
            ModelContextEntryKind.Summary => entry.Content ?? string.Empty,
            _ => string.Empty,
        };
        if (content.Length > MaxEntryHeadChars)
        {
            builder.Append(content, 0, MaxEntryHeadChars).Append("[...]");
        }
        else
        {
            builder.Append(content);
        }
    }
}
