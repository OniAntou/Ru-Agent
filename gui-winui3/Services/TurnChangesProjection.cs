using System.Globalization;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

public sealed record TurnChangeEdit(string ActivityId, string BeforeText, string AfterText,
    int? StartLine, bool HasPreview)
{
    public string Location => StartLine is > 0 ? $"Line {StartLine}" : "Recorded edit";
}

public sealed record TurnChangedFile(string Path, long? Added, long? Removed, IReadOnlyList<TurnChangeEdit> Edits)
{
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    public string Directory => Path[..(Path.LastIndexOf('/') + 1)];
    public string AddedText => TurnChangesProjection.FormatCount(Added, "+");
    public string RemovedText => TurnChangesProjection.FormatCount(Removed, "−");
    public string AccessibleName => $"{Path}, added {AddedText}, removed {RemovedText}. View changes";

    // What the row draws: a count only when there is one to read. An unknown count and
    // "nothing removed" both leave the cell empty instead of a dash; the accessible name
    // above still states the recorded values.
    public bool HasAddedColumn => Added is not null;
    public bool HasRemovedColumn => Removed is > 0;

    /// <summary>The folder as it follows the file name, matching the Changes tab's rows.</summary>
    public string FolderSuffix => Directory.Length == 0 ? string.Empty : "  " + Directory.TrimEnd('/');
}

public sealed record TurnChangeSummary(IReadOnlyList<TurnChangedFile> Files, long? Added, long? Removed)
{
    public static TurnChangeSummary Empty { get; } = new(Array.Empty<TurnChangedFile>(), 0, 0);
    public string Title => Files.Count == 1 ? "Edited 1 file" : $"Edited {Files.Count} files";
    public bool HasAddedTotal => Added is not null;
    public bool HasRemovedTotal => Removed is > 0;
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
        // An observed subagent reply proposes changes in its own workspace; the Subagents
        // panel reviews those, so the main turn summary never claims them.
        if (message.Role != "assistant" || message.IsStreaming || message.ContinuesInLaterReply || message.IsObserved
            || message.AgentActivities is null && message.EarlierReplyActivities is null)
            return TurnChangeSummary.Empty;

        // One card per run: a run split by steers summarizes all its replies' changes here.
        IEnumerable<AgentActivityItem> runActivities = message.EarlierReplyActivities is { } earlier
            ? earlier.Concat(message.AgentActivities ?? Enumerable.Empty<AgentActivityItem>())
            : message.AgentActivities!;

        // Resolve replay duplicates before filtering: a later failure must not
        // resurrect an older success with the same identity.
        var ordered = new List<AgentActivityItem>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var activity in runActivities)
        {
            if (string.IsNullOrWhiteSpace(activity.ActivityId)) continue;
            if (positions.TryGetValue(activity.ActivityId, out int position))
                ordered[position] = activity;
            else
            {
                positions.Add(activity.ActivityId, ordered.Count);
                ordered.Add(activity);
            }
        }

        var edits = ordered
            .Where(activity => activity.Kind == AgentActivityKind.Edit
                && activity.State == AgentActivityState.Completed
                && !string.IsNullOrWhiteSpace(activity.FileName))
            .ToArray();
        string FileKey(AgentActivityItem edit) => edit.FileName!.Replace('\\', '/');

        // The card describes what the turn left standing: a file whose last recorded
        // edit is a deletion is not a changed file, at filesystem independence and
        // without reading the working tree.
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            string file = FileKey(edit);
            if (ChangesTabFiles.IsDeletion(edit)) deleted.Add(file);
            else deleted.Remove(file);
        }

        var files = edits
            .Where(edit => !deleted.Contains(FileKey(edit)))
            .GroupBy(FileKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // A deletion activity carries no line counts; counting it would turn a
                // file that was deleted and later written into an unknown total.
                var counted = group.Where(edit => !ChangesTabFiles.IsDeletion(edit)).ToArray();
                return new TurnChangedFile(group.Key,
                    SumKnown(counted.Select(edit => (long?)edit.Added)),
                    SumKnown(counted.Select(edit => (long?)edit.Removed)),
                    group.Select(edit => new TurnChangeEdit(edit.ActivityId, edit.EditBeforeText,
                        edit.EditAfterText, edit.LineStart, edit.HasEditPreview)).ToArray());
            })
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
