using System.Collections.ObjectModel;
using RuApp.Models;

namespace RuApp.Services;

internal static class AgentLiveSegmentBuilder
{
    // A retry can invalidate text already frozen before a Retry activity.
    // Keep the activity identities and earlier prose; trim superseded slices
    // in place so bindings cannot retain an old answer beside its replacement.
    internal static int Reset(ChatMessageItem message, int offset, int frozen)
    {
        var retained = Math.Clamp(offset, 0, (message.Content ?? string.Empty).Length);
        var cursor = 0;
        if (message.Segments is { } segments)
        {
            foreach (var text in segments.OfType<TextSegment>())
            {
                var length = text.Text.Length;
                text.Text = text.Text[..Math.Clamp(retained - cursor, 0, length)];
                cursor += length;
            }
        }
        return Math.Min(frozen, retained);
    }

    // Call after rendering all preceding deltas. Activity boundaries reflect
    // event order, never a guessed word/line boundary in the model's prose.
    internal static int Freeze(ChatMessageItem message, AgentActivityItem activity, int frozen)
    {
        var content = message.Content ?? string.Empty;
        var start = Math.Clamp(frozen, 0, content.Length);
        var segments = message.Segments ??= new ObservableCollection<AssistantSegment>();
        if (segments.Count > 0 && segments[^1] is TextSegment tail)
        {
            tail.Text = content[start..];
            tail.IsError = message.IsError;
            tail.IsLive = false;
        }
        else if (content.Length > start)
        {
            segments.Add(new TextSegment(content[start..]) { IsError = message.IsError });
        }

        segments.Add(new ActivitySegment(activity.ActivityId, activity));
        segments.Add(new TextSegment(string.Empty) { IsError = message.IsError, IsLive = true });
        return content.Length;
    }
}
