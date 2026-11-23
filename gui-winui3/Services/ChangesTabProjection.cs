using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The Changes tab's projection of the transcript's edit activities, plus the
/// session totals the card and the tab header share.
/// </summary>
/// <param name="Rows">One row per file, most recently edited first, capped at <see cref="ChangesTabProjection.MaxRows"/>.</param>
/// <param name="Added">Added lines of the hunks every row shows, summed over every file.</param>
/// <param name="Removed">Removed lines of the hunks every row shows, summed over every file.</param>
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
    /// Edits kept per file row for identity and reveal lookup; the row draws the newest
    /// recorded edit's hunk only.
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

        var groups = live
            .Select((edit, order) => (Edit: edit, Order: order))
            .GroupBy(item => FileKey(item.Edit), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(item => item.Order))
            .ToList();

        // The numbers beside a file, and the totals under the tab, describe the hunks each
        // row draws: one per recorded edit, so a total is the sum of what the reader can see.
        var previews = groups.ToDictionary(
            group => group,
            group => PreviewOf(group.Select(item => item.Edit).ToList()));
        int added = previews.Values.Sum(preview => preview.Added ?? 0);
        int removed = previews.Values.Sum(preview => preview.Removed ?? 0);

        var visible = groups.Take(MaxRows).ToList();
        // A click on an older turn must still reveal its edit without lifting
        // the session panel's row bound or changing its session-wide totals.
        if (focusedActivityId is not null && groups.Count > MaxRows
            && !visible.Any(group => group.Any(item => item.Edit.ActivityId == focusedActivityId))
            && groups.FirstOrDefault(group => group.Any(item => item.Edit.ActivityId == focusedActivityId)) is { } focused)
        {
            visible[^1] = focused;
        }

        var rows = visible.Select(group => Row(group.Select(item => item.Edit).ToList(), previews[group],
            focusedActivityId, group.Any(item => item.Edit.RelativePath is not null) ? group.Key : null)).ToList();
        return new ChangesTabProjectionResult(rows, added, removed);
    }

    /// <summary>
    /// The one hunk a row shows — its newest recorded edit — and the numbers beside it: that
    /// edit's own counts, so the number and the block under it stay one story. A file whose
    /// edits recorded no text (an image, another tool's edit) keeps the counts its tool
    /// reported, since there is no hunk to read them from.
    /// </summary>
    private static (ChangesTabEdit[] Hunks, int? Added, int? Removed) PreviewOf(
        IReadOnlyList<AgentActivityItem> fileEdits)
    {
        if (fileEdits.LastOrDefault(edit => edit.HasEditPreview) is not { } newest)
        {
            return (Array.Empty<ChangesTabEdit>(),
                SumKnown(fileEdits.Select(edit => edit.Added)),
                SumKnown(fileEdits.Select(edit => edit.Removed)));
        }

        // An edit made through an absolute path carries only its basename; the row's own
        // name picks the syntax colors when the edit has none.
        var hunk = new ChangesTabEdit(
            newest.ActivityId,
            newest.EditBeforeText,
            newest.EditAfterText,
            newest.LineStart,
            newest.Added,
            newest.Removed,
            newest.FileName is { Length: > 0 } ? newest.FileName : fileEdits[^1].FileName ?? string.Empty);
        return ([hunk], hunk.Added, hunk.Removed);
    }

    private static ChangesTabRow Row(
        List<AgentActivityItem> fileEdits,
        (ChangesTabEdit[] Hunks, int? Added, int? Removed) preview,
        string? focusedActivityId,
        string? relativePath)
    {
        var newestFirst = fileEdits.AsEnumerable().Reverse().Take(MaxEditsPerRow).ToList();
        // The focused edit stays reachable even when it is older than the row's cap.
        if (focusedActivityId is not null && newestFirst.All(edit => edit.ActivityId != focusedActivityId)
            && fileEdits.FirstOrDefault(edit => edit.ActivityId == focusedActivityId) is { } focused)
        {
            newestFirst[^1] = focused;
        }

        return new ChangesTabRow(
            fileEdits[^1].FileName is { Length: > 0 } name ? name.Replace('\\', '/') : UnnamedFile,
            preview.Added,
            preview.Removed,
            newestFirst.Select(edit => new ChangesTabEdit(edit.ActivityId, edit.EditBeforeText, edit.EditAfterText,
                edit.LineStart, edit.Added, edit.Removed, edit.FileName)).ToArray(),
            preview.Hunks,
            fileEdits[0].ActivityId,
            relativePath);
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
