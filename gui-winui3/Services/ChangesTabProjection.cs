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

    public static ChangesTabProjectionResult Project(IEnumerable<AgentActivityItem> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var edits = new List<AgentActivityItem>();
        int added = 0;
        int removed = 0;
        foreach (var activity in activities)
        {
            if (activity.Kind != AgentActivityKind.Edit)
            {
                continue;
            }

            edits.Add(activity);
            added += activity.Added ?? 0;
            removed += activity.Removed ?? 0;
        }

        var rows = new List<ChangesTabRow>(Math.Min(edits.Count, MaxRows));
        for (var index = edits.Count - 1; index >= 0 && rows.Count < MaxRows; index--)
        {
            var activity = edits[index];
            rows.Add(new ChangesTabRow(
                activity.FileName ?? string.Empty,
                activity.Added ?? 0,
                activity.Removed ?? 0,
                activity.EditBeforeText,
                activity.EditAfterText,
                activity.LineStart,
                activity.SafeCode));
        }

        return new ChangesTabProjectionResult(rows, added, removed);
    }
}
