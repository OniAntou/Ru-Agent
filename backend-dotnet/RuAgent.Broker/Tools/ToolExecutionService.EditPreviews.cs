using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private static string BuildEditPreview(JsonArray? edits, string? fileText)
    {
        if (edits is null || edits.Count == 0) return string.Empty;
        if (edits.Count == 1) return EditPreview(edits[0]!.AsObject(), fileText);

        const int maximumExcerpts = 4;
        var shown = Math.Min(edits.Count, maximumExcerpts);
        var header = $"{edits.Count} edits in one file. Excerpts: {shown} of {edits.Count}.\n";
        var footer = shown < edits.Count
            ? $"\n{edits.Count - shown} more edits not shown. Approval applies all {edits.Count} edits."
            : string.Empty;
        // Reserve labels and line breaks before distributing the shared UI bound.
        var perEdit = (ApprovalPreviewFields.MaxLength - header.Length - footer.Length - shown * 4) / shown;
        var preview = new StringBuilder(header);
        for (var index = 0; index < shown; index++)
        {
            if (index > 0) preview.Append('\n');
            preview.Append(index + 1).Append(". ").Append(EditPreview(edits[index]!.AsObject(), fileText, perEdit));
        }
        return preview.Append(footer).ToString();
    }

    private static string EditPreview(JsonObject arguments, string? fileText, int maxLength = ApprovalPreviewFields.MaxLength)
    {
        var oldString = ReadOptionalString(arguments, "old_string") ?? string.Empty;
        var newString = ReadOptionalString(arguments, "new_string") ?? string.Empty;
        if (oldString.Length == 0 && newString.Length == 0)
        {
            return string.Empty;
        }

        var matchIndex = oldString.Length > 0 && fileText is not null
            ? fileText.IndexOf(oldString, StringComparison.Ordinal)
            : -1;
        if (matchIndex < 0)
        {
            var headLength = (maxLength - 8) / 2;
            return $"[-{PreviewHead(oldString, headLength)}-][+{PreviewHead(newString, headLength)}+]";
        }

        var before = fileText.AsSpan(0, matchIndex);
        var after = fileText.AsSpan(matchIndex + oldString.Length);
        var maxHeadLength = (maxLength - 8) / 2;
        var core = $"[-{PreviewHead(oldString, maxHeadLength)}-][+{PreviewHead(newString, maxHeadLength)}+]";
        var budget = maxLength - core.Length;
        if (budget <= 0)
        {
            return core;
        }

        var afterCount = Math.Min(after.Length, budget / 2);
        var beforeCount = Math.Min(before.Length, budget - afterCount);
        afterCount = Math.Min(after.Length, budget - beforeCount);
        return $"{PreviewTail(before, beforeCount)}{core}{PreviewHead(after, afterCount)}";
    }

    private static string PreviewHead(ReadOnlySpan<char> text, int limit)
    {
        var length = Math.Min(text.Length, limit);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length].ToString();
    }

    private static string PreviewTail(ReadOnlySpan<char> text, int limit)
    {
        var start = Math.Max(0, text.Length - limit);
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
        return text[start..].ToString();
    }
}
