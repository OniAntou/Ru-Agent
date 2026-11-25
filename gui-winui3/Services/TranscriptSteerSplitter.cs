using System.Collections.ObjectModel;
using System.Linq;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Shows a run's steers the way any message is shown (ADR 0056): each steer the run read
/// becomes the user's own message, and Ru's reply splits around it into the reply before and
/// the reply after, as if the two had simply kept messaging. The run and its history are
/// unchanged; this only decides which bubbles the replayed run is drawn as.
/// </summary>
internal static class TranscriptSteerSplitter
{
    /// <summary>
    /// The bubbles a replayed assistant message is drawn as: itself when it holds no steer,
    /// otherwise its reply parts with a user message between them. The final part keeps the
    /// original instance, which carries the run's answer.
    /// </summary>
    internal static IReadOnlyList<ChatMessageItem> Split(ChatMessageItem message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != "assistant"
            || message.Segments is not { } segments
            || !segments.Any(segment => segment is ActivitySegment { IsSteer: true }))
        {
            return new[] { message };
        }

        var shown = new List<ChatMessageItem>();
        var earlier = new List<AgentActivityItem>();
        var part = new List<AssistantSegment>();
        foreach (var segment in segments)
        {
            if (segment is not ActivitySegment { IsSteer: true } steer)
            {
                part.Add(segment);
                continue;
            }

            if (HasContent(part))
            {
                var before = ReplyPart(message, part, reuse: null);
                before.ContinuesInLaterReply = true;
                if (before.AgentActivities is { } activities) earlier.AddRange(activities);
                shown.Add(before);
            }

            part = new List<AssistantSegment>();
            var (written, imageCount) = RuAgent.Protocol.Harness.AgentSteerText.Split(steer.Item.SteerText);
            var said = new ChatMessageItem
            {
                Role = "user",
                Content = written,
                SessionId = message.SessionId,
                SourceRunId = message.SourceRunId,
                SourceSteerId = ReplayImageAssignment.SteerIdFromActivityId(steer.Item.ActivityId),
                Attachments = new ObservableCollection<ImageAttachmentItem>(),
                MissingImageCount = imageCount,
            };
            // Files the steer carried show as chips and its images as a count, as on a reopened turn.
            AttachedPromptDisplay.Apply(said);
            shown.Add(said);
        }

        // The reply after the last steer keeps the original instance, even when it is still
        // empty: a run that read the steer and is still working continues in it.
        var last = ReplyPart(message, part, reuse: message);
        last.EarlierReplyActivities = earlier.Count > 0 ? earlier : null;
        shown.Add(last);
        return shown;
    }

    private static bool HasContent(IReadOnlyList<AssistantSegment> part) =>
        part.Any(segment => segment is ActivitySegment || segment is TextSegment { HasText: true });

    private static ChatMessageItem ReplyPart(ChatMessageItem source, List<AssistantSegment> part, ChatMessageItem? reuse)
    {
        var target = reuse ?? new ChatMessageItem
        {
            Role = source.Role,
            SessionId = source.SessionId,
            SourceRunId = source.SourceRunId,
            SourceMessageIndex = source.SourceMessageIndex,
        };
        var activities = part.OfType<ActivitySegment>().Select(segment => segment.Item).ToList();
        target.Content = string.Concat(part.OfType<TextSegment>().Select(text => text.Text));
        target.AgentActivities = activities.Count > 0 ? new ObservableCollection<AgentActivityItem>(activities) : null;
        target.ActivitySplitOffsets = null;
        target.Segments = new ObservableCollection<AssistantSegment>(part);
        return target;
    }
}
