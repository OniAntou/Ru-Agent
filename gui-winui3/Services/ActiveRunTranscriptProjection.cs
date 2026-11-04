using System.Collections.Generic;
using System.Collections.ObjectModel;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>Reattaches a replayed assistant row to the live feed for an adopted run.</summary>
internal static class ActiveRunTranscriptProjection
{
    internal static ChatMessageItem Adopt(
        IList<ChatMessageItem> messages,
        ObservableCollection<AgentActivityItem> liveActivities,
        string sessionId,
        string runId,
        DateTime? runStartedAt = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(liveActivities);

        var active = messages.LastOrDefault(message =>
            message.Role == "assistant"
            && string.Equals(message.SourceRunId, runId, StringComparison.Ordinal));
        if (active is null)
        {
            active = new ChatMessageItem
            {
                Role = "assistant",
                SessionId = sessionId,
                SourceRunId = runId,
                IsStreaming = true,
                AgentActivities = liveActivities,
            };
            messages.Add(active);
        }

        // The live clock counts from the bubble's timestamp. A bubble rebuilt on
        // reopening the session was stamped "now", so switching away and back reset
        // the turn's elapsed time; the Host's run start is the durable origin.
        if (runStartedAt is { } started && started < active.Timestamp) active.Timestamp = started;

        foreach (var message in messages)
        {
            if (message.Role == "assistant" && !ReferenceEquals(message, active))
            {
                message.IsStreaming = false;
            }
        }

        if (!ReferenceEquals(active.AgentActivities, liveActivities))
        {
            var replayed = active.AgentActivities?.ToArray() ?? [];
            var alreadyPresent = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<AgentActivityItem>(replayed.Length + liveActivities.Count);
            foreach (var item in replayed)
            {
                if (alreadyPresent.Add(item.ActivityId)) merged.Add(item);
            }
            foreach (var item in liveActivities)
            {
                if (alreadyPresent.Add(item.ActivityId)) merged.Add(item);
            }

            liveActivities.Clear();
            foreach (var item in merged) liveActivities.Add(item);
            active.AgentActivities = liveActivities;
        }

        active.SessionId ??= sessionId;
        active.SourceRunId = runId;
        active.IsStreaming = true;

        return active;
    }
}
