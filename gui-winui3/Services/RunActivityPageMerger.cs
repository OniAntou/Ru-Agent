using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Joins an older replay page onto what a reopened run already shows. Pure, so the order and
/// de-duplication rules are unit-testable: rows come back in durable order, the checklist the
/// newest page carried ahead of itself keeps only its own place once its page arrives, and
/// the cursor moves to the page just read.
/// </summary>
internal static class RunActivityPageMerger
{
    /// <summary>The replay page's rows as feed items owned by the run.</summary>
    internal static AgentActivityItem[] Items(ApiService.RunActivityReplay page, string runId) =>
        page.Activities.Select(model => new AgentActivityItem(model)
        {
            OwnerRunId = runId,
            ReplaySequence = model.DurableSequence,
        }).ToArray();

    internal static SessionRunReplay Merge(SessionRunReplay shown, ApiService.RunActivityReplay older)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(older);
        var olderItems = Items(older, shown.RunId);
        var olderIds = olderItems.Select(item => item.ActivityId).ToHashSet(StringComparer.Ordinal);
        var activities = olderItems
            .Concat(shown.Activities.Where(item => !olderIds.Contains(item.ActivityId)))
            // Stable: rows of one event (a tool row and its checklist) keep their order.
            .OrderBy(item => item.ReplaySequence ?? long.MaxValue)
            .ToArray();

        var seenActivities = new HashSet<string>(StringComparer.Ordinal);
        var joined = older.Transcript.Concat(shown.Transcript)
            .Where(entry => entry.ActivityId is not { } id || seenActivities.Add(id))
            .ToList();
        // A page without narration has no entries; once another page brings some, the segment
        // builder would drop its rows. Each row keeps an entry of its own.
        if (joined.Count > 0)
        {
            joined.AddRange(activities.Where(item => item.ReplaySequence is not null && seenActivities.Add(item.ActivityId))
                .Select(item => new ApiService.RunTranscriptEntry(item.ReplaySequence!.Value, "activity", null, item.ActivityId)));
        }

        var transcript = joined.OrderBy(entry => entry.Sequence).ToArray();
        return new SessionRunReplay(shown.RunId, activities, transcript, older.NextBeforeSequence, older.OlderCount);
    }
}
