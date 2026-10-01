using System.Collections.ObjectModel;
using System.Text;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Builds a reopened bubble's chronological segments from a run replay: narration
/// chunks become prose slices interleaved with the activity rows that followed
/// them, the same shape the live turn assembles while streaming. Pure ordering
/// over the host's event-ordered transcript, so it is unit-testable in isolation.
/// Unknown activity ids are skipped: the text still replays instead of the whole
/// bubble falling back to the grouped feed.
/// </summary>
internal static class AgentReplaySegmentBuilder
{
    public sealed record BuiltReplay(
        string Content,
        ObservableCollection<AssistantSegment> Segments,
        List<ActivitySplitOffset> Offsets);

    public static BuiltReplay? Build(
        IReadOnlyList<AgentActivityItem> items,
        IReadOnlyList<ApiService.RunTranscriptEntry> transcript)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(transcript);
        if (transcript.Count == 0)
        {
            return null;
        }

        var byId = new Dictionary<string, AgentActivityItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not null && !byId.ContainsKey(item.ActivityId))
            {
                byId[item.ActivityId] = item;
            }
        }

        var segments = new ObservableCollection<AssistantSegment>();
        var offsets = new List<ActivitySplitOffset>();
        var content = new StringBuilder();
        bool any = false;
        foreach (var entry in transcript.OrderBy(item => item.Sequence))
        {
            if (string.Equals(entry.Kind, "text", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(entry.Text))
            {
                content.Append(entry.Text);
                segments.Add(new TextSegment(entry.Text));
                any = true;
            }
            else if (string.Equals(entry.Kind, "activity", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.ActivityId)
                && byId.TryGetValue(entry.ActivityId, out var item))
            {
                offsets.Add(new ActivitySplitOffset(entry.ActivityId, content.Length));
                segments.Add(new ActivitySegment(entry.ActivityId, item));
                any = true;
            }
        }

        return any ? new BuiltReplay(content.ToString(), segments, offsets) : null;
    }
}
