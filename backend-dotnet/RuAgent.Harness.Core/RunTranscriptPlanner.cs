using System.Text.Json.Serialization;

namespace RuAgent.Harness.Core;

/// <summary>
/// One narration chunk of a run: the model's own words from a single model step,
/// positioned by the durable event sequence of the step that produced it.
/// </summary>
public sealed record TranscriptChunk(long Sequence, string Text);

/// <summary>
/// One ordered replay unit: either a narration chunk or a pointer to an activity
/// row the activity replay already emits. The GUI renders them in order, which is
/// what rebuilds the live feed's interleave on a reopened session.
/// </summary>
public sealed record TranscriptEntry(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("activity_id")] string? ActivityId = null);

/// <summary>
/// Merges durable narration chunks with activity positions into one event-ordered
/// replay. Pure ordering: no I/O, no policy, so it is unit-testable in isolation.
/// </summary>
public static class RunTranscriptPlanner
{
    public const string TextKind = "text";

    public const string ActivityKind = "activity";

    public static IReadOnlyList<TranscriptEntry> Merge(
        IReadOnlyList<(long Sequence, string ActivityId)> activities,
        IReadOnlyList<TranscriptChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(chunks);
        var orderedActivities = activities.OrderBy(activity => activity.Sequence).ToList();
        var orderedChunks = chunks.OrderBy(chunk => chunk.Sequence).ToList();
        var merged = new List<TranscriptEntry>(orderedActivities.Count + orderedChunks.Count);
        int activityIndex = 0;
        int chunkIndex = 0;
        while (activityIndex < orderedActivities.Count || chunkIndex < orderedChunks.Count)
        {
            // A tie puts the activity row first: a thinking row opens its step while
            // the step text only completes it, so the row is the earlier moment.
            bool takeActivity = chunkIndex >= orderedChunks.Count
                || (activityIndex < orderedActivities.Count
                    && orderedActivities[activityIndex].Sequence <= orderedChunks[chunkIndex].Sequence);
            if (takeActivity)
            {
                var (sequence, activityId) = orderedActivities[activityIndex++];
                merged.Add(new TranscriptEntry(sequence, ActivityKind, ActivityId: activityId));
            }
            else
            {
                var chunk = orderedChunks[chunkIndex++];
                merged.Add(new TranscriptEntry(chunk.Sequence, TextKind, Text: chunk.Text));
            }
        }

        return merged;
    }
}
