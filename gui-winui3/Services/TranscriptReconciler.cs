using System;
using System.Collections.Generic;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Merges a reloaded transcript into the bound one row by row instead of resetting it.
/// </summary>
/// <remarks>
/// A Reset makes the ListView drop every container, re-parse every visible Markdown
/// block and paint the top of the transcript before the scroll position comes back,
/// which reads as the whole chat blinking at the end of a reply. An unchanged row keeps
/// its instance, so its container, text selection and expanded rows stay as they are;
/// only rows whose content changed are replaced. Rows are aligned by position: a reload
/// of the same session changes its tail, not its middle.
/// </remarks>
internal static class TranscriptReconciler
{
    internal readonly record struct Result(int Kept, int Replaced, int Added, int Removed, bool KeptLiveReply);

    /// <param name="liveReply">
    /// The bubble that just streamed, or null. It is kept when the reload holds the same
    /// reply for its run: its segments already show that text, and rebuilding them would
    /// repaint the row the reader is looking at.
    /// </param>
    internal static Result Apply(
        IList<ChatMessageItem> current,
        IReadOnlyList<ChatMessageItem> restored,
        ChatMessageItem? liveReply = null,
        string? liveRunId = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(restored);

        int kept = 0, replaced = 0, added = 0;
        bool keptLive = false;
        for (int index = 0; index < restored.Count; index++)
        {
            var next = restored[index];
            if (index >= current.Count)
            {
                current.Add(next);
                added++;
                continue;
            }

            var existing = current[index];
            if (ReferenceEquals(existing, next))
            {
                kept++;
                continue;
            }

            if (liveReply is not null && ReferenceEquals(existing, liveReply) && IsSameLiveReply(existing, next, liveRunId))
            {
                AdoptLiveReply(existing, next);
                keptLive = true;
                kept++;
                continue;
            }

            if (IsEquivalent(existing, next))
            {
                AdoptIdentity(existing, next);
                kept++;
                continue;
            }

            current[index] = next;
            replaced++;
        }

        int removed = 0;
        while (current.Count > restored.Count)
        {
            current.RemoveAt(current.Count - 1);
            removed++;
        }

        return new Result(kept, replaced, added, removed, keptLive);
    }

    // The live bubble may still carry display markers the history strips, so its text
    // is compared after the caller has already normalised it into the restored row.
    private static bool IsSameLiveReply(ChatMessageItem live, ChatMessageItem next, string? runId) =>
        string.Equals(next.Role, "assistant", StringComparison.Ordinal)
        && string.Equals(live.Role, "assistant", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(runId)
        && string.Equals(next.SourceRunId, runId, StringComparison.Ordinal)
        && string.Equals(ChatDisplayMarkers.Strip(live.Content), next.Content, StringComparison.Ordinal)
        && live.IsError == next.IsError;

    private static void AdoptLiveReply(ChatMessageItem live, ChatMessageItem next)
    {
        live.Content = next.Content;
        AdoptIdentity(live, next);
        // The restored row holds a snapshot of the same activity items the live segments
        // point at. Taking it detaches the bubble from the run's shared collection, which
        // the next turn clears, without rebuilding the segments on screen.
        if (next.AgentActivities is not null) live.AgentActivities = next.AgentActivities;
        if (live.Segments is null && next.Segments is not null) live.Segments = next.Segments;
        live.IsStreaming = false;
        live.SettleSegments();
    }

    private static void AdoptIdentity(ChatMessageItem existing, ChatMessageItem next)
    {
        existing.SourceRunId = next.SourceRunId ?? existing.SourceRunId;
        existing.SourceMessageIndex = next.SourceMessageIndex ?? existing.SourceMessageIndex;
        existing.SessionId = next.SessionId ?? existing.SessionId;
        existing.ActivitySplitOffsets = next.ActivitySplitOffsets ?? existing.ActivitySplitOffsets;
        // History carries no usage for older turns; dropping the label would collapse
        // its row and shift everything below it.
        existing.Usage = next.Usage ?? existing.Usage;
        existing.IsStreaming = next.IsStreaming;
    }

    /// <summary>Whether two rows render the same, so the one on screen can stay.</summary>
    internal static bool IsEquivalent(ChatMessageItem existing, ChatMessageItem next)
    {
        if (!string.Equals(existing.Role, next.Role, StringComparison.Ordinal)
            || !string.Equals(existing.Content, next.Content, StringComparison.Ordinal)
            || existing.IsError != next.IsError
            || !string.Equals(existing.ActionKind, next.ActionKind, StringComparison.Ordinal)
            || !string.Equals(existing.ActionLabel, next.ActionLabel, StringComparison.Ordinal))
        {
            return false;
        }

        // A row sent or streamed locally has no durable identity yet and adopts the
        // reloaded one; a row that already has one must name the same message.
        if (existing.SourceMessageIndex is { } index && next.SourceMessageIndex is { } nextIndex && index != nextIndex)
            return false;
        if (existing.SourceRunId is { } run && next.SourceRunId is { } nextRun
            && !string.Equals(run, nextRun, StringComparison.Ordinal))
            return false;

        return SameActivities(existing.AgentActivities, next.AgentActivities)
            && SameSegments(existing.Segments, next.Segments);
    }

    private static bool SameActivities(IReadOnlyList<AgentActivityItem>? left, IReadOnlyList<AgentActivityItem>? right)
    {
        int leftCount = left?.Count ?? 0;
        if (leftCount != (right?.Count ?? 0)) return false;
        for (int index = 0; index < leftCount; index++)
        {
            var a = left![index];
            var b = right![index];
            if (!string.Equals(a.ActivityId, b.ActivityId, StringComparison.Ordinal)
                || a.Kind != b.Kind
                || a.State != b.State
                || !string.Equals(a.DisplayText, b.DisplayText, StringComparison.Ordinal)
                || !string.Equals(a.Detail, b.Detail, StringComparison.Ordinal)
                || a.DurationMs != b.DurationMs)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameSegments(IReadOnlyList<AssistantSegment>? left, IReadOnlyList<AssistantSegment>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            bool same = (left[index], right[index]) switch
            {
                (TextSegment a, TextSegment b) => string.Equals(a.Text, b.Text, StringComparison.Ordinal),
                (ActivitySegment a, ActivitySegment b) => string.Equals(a.ActivityId, b.ActivityId, StringComparison.Ordinal),
                _ => false,
            };
            if (!same) return false;
        }

        return true;
    }
}
