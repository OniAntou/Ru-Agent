using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The Changes tab's projection of the transcript's edit activities, plus the
/// session totals the card and the tab header share.
/// </summary>
/// <param name="Rows">One row per file, most recently edited first, capped at <see cref="ChangesTabProjection.MaxRows"/>.</param>
/// <param name="Added">Added lines summed over every completed edit.</param>
/// <param name="Removed">Removed lines summed over every completed edit.</param>
public sealed record ChangesTabProjectionResult(
    IReadOnlyList<ChangesTabRow> Rows,
    int Added,
    int Removed);

/// <summary>
/// Builds the Changes tab from the feed's edit activities: completed edits only,
/// one row per file, most recently edited file first, capped so a long session
/// cannot grow the panel without bound. Pure and view-free so the grouping, the
/// ordering, the caps and the totals are testable.
/// </summary>
public static class ChangesTabProjection
{
    public const int MaxRows = 100;

    /// <summary>
    /// Edits kept per file row for identity and reveal lookup; the row draws one
    /// comparison between the file's oldest and newest recorded version instead.
    /// </summary>
    public const int MaxEditsPerRow = 25;

    /// <summary>Shown for an edit whose activity named no file (an apply_patch call).</summary>
    public const string UnnamedFile = "Patch";

    public static ChangesTabProjectionResult ProjectSession(
        IEnumerable<ChatMessageItem> messages,
        IEnumerable<AgentActivityItem> liveActivities,
        string? focusedActivityId = null) =>
        Project(messages.SelectMany(message =>
            message.AgentActivities is { } activities
                ? (IEnumerable<AgentActivityItem>)activities
                : Array.Empty<AgentActivityItem>()).Concat(liveActivities), focusedActivityId);

    public static ChangesTabProjectionResult Project(IEnumerable<AgentActivityItem> activities, string? focusedActivityId = null)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var edits = new List<AgentActivityItem>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var activity in activities)
        {
            if (activity.Kind != AgentActivityKind.Edit)
            {
                continue;
            }

            // Replayed messages can share their current run's live collection.
            // The live copy wins without moving that edit to a different turn.
            if (positions.TryGetValue(activity.ActivityId, out int position))
            {
                edits[position] = activity;
            }
            else
            {
                positions.Add(activity.ActivityId, edits.Count);
                edits.Add(activity);
            }
        }

        // A failed, denied or still running edit changed nothing (yet): listing it
        // showed every retry of a stale edit as another +0 −0 row.
        var completed = edits.Where(edit => edit.State == AgentActivityState.Completed).ToList();

        // A basename-only edit (made through an absolute path) joins the relative
        // path with its name when exactly one exists; otherwise it groups by name.
        var pathsByName = completed
            .Where(edit => edit.RelativePath is not null)
            .GroupBy(edit => edit.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(edit => edit.RelativePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        string FileKey(AgentActivityItem edit) =>
            edit.RelativePath
            ?? (edit.FileName is { Length: > 0 } name
                ? pathsByName.TryGetValue(name, out var paths) && paths.Length == 1 ? paths[0] : name
                : UnnamedFile);

        // The tab lists what still stands. Walk each file's edits in order: a deletion
        // removes it, a later write or restore brings it back. A deleted file is then
        // neither a row nor part of the totals.
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in completed)
        {
            string file = FileKey(edit);
            if (ChangesTabFiles.IsDeletion(edit)) deleted.Add(file);
            else deleted.Remove(file);
        }

        var live = completed.Where(edit => !deleted.Contains(FileKey(edit))).ToList();
        int added = live.Sum(edit => edit.Added ?? 0);
        int removed = live.Sum(edit => edit.Removed ?? 0);

        var groups = live
            .Select((edit, order) => (Edit: edit, Order: order))
            .GroupBy(item => FileKey(item.Edit), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(item => item.Order))
            .ToList();
        var visible = groups.Take(MaxRows).ToList();
        // A click on an older turn must still reveal its edit without lifting
        // the session panel's row bound or changing its session-wide totals.
        if (focusedActivityId is not null && groups.Count > MaxRows
            && !visible.Any(group => group.Any(item => item.Edit.ActivityId == focusedActivityId))
            && groups.FirstOrDefault(group => group.Any(item => item.Edit.ActivityId == focusedActivityId)) is { } focused)
        {
            visible[^1] = focused;
        }

        var rows = visible.Select(group => Row(group.Select(item => item.Edit).ToList(), focusedActivityId,
            group.Any(item => item.Edit.RelativePath is not null) ? group.Key : null)).ToList();
        return new ChangesTabProjectionResult(rows, added, removed);
    }

    private static ChangesTabRow Row(List<AgentActivityItem> fileEdits, string? focusedActivityId, string? relativePath)
    {
        var newestFirst = fileEdits.AsEnumerable().Reverse().Take(MaxEditsPerRow).ToList();
        // The focused edit stays reachable even when it is older than the row's cap.
        if (focusedActivityId is not null && newestFirst.All(edit => edit.ActivityId != focusedActivityId)
            && fileEdits.FirstOrDefault(edit => edit.ActivityId == focusedActivityId) is { } focused)
        {
            newestFirst[^1] = focused;
        }

        // The row compares the file's recorded versions. Prefer the first and last edit
        // that actually carries a preview: an edit whose detail is unavailable (an
        // older/other tool shape) must not hide a comparison the rest of the row has.
        var previewEdits = fileEdits.Where(edit => edit.HasEditPreview).ToArray();
        var beforeSource = previewEdits.Length > 0 ? previewEdits[0] : fileEdits[0];
        var afterSource = previewEdits.Length > 0 ? previewEdits[^1] : fileEdits[^1];
        return new ChangesTabRow(
            fileEdits[^1].FileName is { Length: > 0 } name ? name.Replace('\\', '/') : UnnamedFile,
            SumKnown(fileEdits.Select(edit => edit.Added)),
            SumKnown(fileEdits.Select(edit => edit.Removed)),
            newestFirst.Select(edit => new ChangesTabEdit(edit.ActivityId, edit.EditBeforeText, edit.EditAfterText,
                edit.LineStart, edit.Added, edit.Removed, edit.FileName)).ToArray(),
            fileEdits[0].ActivityId,
            relativePath,
            // One comparison, not a log of every edit: the earliest recorded version that
            // has evidence against the latest one, taken before the per-row cap narrows
            // the stored edit window.
            previewBeforeText: beforeSource.EditBeforeText,
            previewAfterText: afterSource.EditAfterText,
            previewStartLine: beforeSource.LineStart);
    }

    /// <summary>The sum of the known counts, or null when no edit of the file reported one.</summary>
    private static int? SumKnown(IEnumerable<int?> counts)
    {
        int? total = null;
        foreach (var count in counts)
            if (count is { } value) total = (total ?? 0) + value;
        return total;
    }
}
