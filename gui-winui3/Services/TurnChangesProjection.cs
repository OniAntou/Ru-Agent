using System.Globalization;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

public sealed record TurnChangeEdit(string ActivityId, string BeforeText, string AfterText,
    int? StartLine, bool HasPreview)
{
    public bool HasNoPreview => !HasPreview;
    public string Location => StartLine is > 0 ? $"Line {StartLine}" : "Recorded edit";
}

public sealed record TurnChangedFile(string Path, long? Added, long? Removed, IReadOnlyList<TurnChangeEdit> Edits)
{
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    public string Directory => Path[..(Path.LastIndexOf('/') + 1)];
    public string AddedText => TurnChangesProjection.FormatCount(Added, "+");
    public string RemovedText => TurnChangesProjection.FormatCount(Removed, "−");
    public string AccessibleName => $"{Path}, added {AddedText}, removed {RemovedText}. View changes";
}

public sealed record TurnChangeSummary(IReadOnlyList<TurnChangedFile> Files, long? Added, long? Removed)
{
    public static TurnChangeSummary Empty { get; } = new(Array.Empty<TurnChangedFile>(), 0, 0);
    public string Title => Files.Count == 1 ? "Edited 1 file" : $"Edited {Files.Count} files";
    public string AddedText => TurnChangesProjection.FormatCount(Added, "+");
    public string RemovedText => TurnChangesProjection.FormatCount(Removed, "−");
}

/// <summary>
/// A settled assistant turn's recorded edits, independent of the session Changes
/// tab and today's filesystem. Counts describe edit operations, not a net Git diff.
/// </summary>
public static class TurnChangesProjection
{
    public const int PreviewFileCount = 4;

    public static TurnChangeSummary ForMessage(ChatMessageItem message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != "assistant" || message.IsStreaming || message.AgentActivities is null)
            return TurnChangeSummary.Empty;

        // Resolve replay duplicates before filtering: a later failure must not
        // resurrect an older success with the same identity.
        var latest = new Dictionary<string, AgentActivityItem>(StringComparer.Ordinal);
        foreach (var activity in message.AgentActivities)
            if (!string.IsNullOrWhiteSpace(activity.ActivityId)) latest[activity.ActivityId] = activity;

        var files = latest.Values
            .Where(activity => activity.Kind == AgentActivityKind.Edit
                && activity.State == AgentActivityState.Completed
                && !string.IsNullOrWhiteSpace(activity.FileName))
            .GroupBy(activity => activity.FileName!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Select(group => new TurnChangedFile(group.Key,
                SumKnown(group.Select(edit => (long?)edit.Added)),
                SumKnown(group.Select(edit => (long?)edit.Removed)),
                group.Select(edit => new TurnChangeEdit(edit.ActivityId, edit.EditBeforeText,
                    edit.EditAfterText, edit.LineStart, edit.HasEditPreview)).ToArray()))
            .ToArray();
        return new(files, SumKnown(files.Select(file => file.Added)), SumKnown(files.Select(file => file.Removed)));
    }

    internal static string FormatCount(long? count, string sign) => count is { } value
        ? sign + value.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private static long? SumKnown(IEnumerable<long?> counts)
    {
        long total = 0;
        foreach (var count in counts)
        {
            if (count is null or < 0) return null;
            total += count.Value;
        }
        return total;
    }
}
