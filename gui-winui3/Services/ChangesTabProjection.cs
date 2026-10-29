using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The Changes tab's projection of the transcript's edit activities, plus the
/// session totals the card and the tab header share.
/// </summary>
/// <param name="Rows">Newest first, capped at <see cref="ChangesTabProjection.MaxRows"/>.</param>
/// <param name="Added">Added lines summed over the uncapped set.</param>
/// <param name="Removed">Removed lines summed over the uncapped set.</param>
public sealed record ChangesTabProjectionResult(
    IReadOnlyList<ChangesTabRow> Rows,
    int Added,
    int Removed);

/// <summary>
/// Builds the Changes tab from the feed's edit activities: newest first, capped
/// so a long session cannot grow the panel without bound. Pure and view-free so
/// the ordering, the cap, and the totals are testable.
/// </summary>
public static class ChangesTabProjection
{
    public const int MaxRows = 100;

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
        int added = 0;
        int removed = 0;
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

        foreach (var activity in edits)
        {
            added += activity.Added ?? 0;
            removed += activity.Removed ?? 0;
        }

        var visible = edits.TakeLast(MaxRows).ToList();
        // A click on an older turn must still reveal its edit without lifting
        // the session panel's row bound or changing its session-wide totals.
        if (focusedActivityId is not null && positions.TryGetValue(focusedActivityId, out int focusedIndex)
            && focusedIndex < edits.Count - MaxRows)
        {
            visible.RemoveAt(0);
            visible.Insert(0, edits[focusedIndex]);
        }
        var rows = new List<ChangesTabRow>(visible.Count);
        for (var index = visible.Count - 1; index >= 0; index--)
        {
            var activity = visible[index];
            rows.Add(new ChangesTabRow(
                activity.FileName ?? string.Empty,
                activity.Added ?? 0,
                activity.Removed ?? 0,
                activity.EditBeforeText,
                activity.EditAfterText,
                activity.LineStart,
                activity.SafeCode,
                activity.ActivityId));
        }

        return new ChangesTabProjectionResult(rows, added, removed);
    }
}
