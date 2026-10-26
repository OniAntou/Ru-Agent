using System.Collections.ObjectModel;
using RuApp.Models;

namespace RuApp.Services;

public sealed record SessionHistoryEntry(
    string Role,
    string Content,
    string? SourceRunId,
    int? MessageIndex = null,
    string? SourceMessageId = null);

public sealed record SessionHistoryPage(
    string SessionId,
    IReadOnlyList<SessionHistoryEntry> Messages,
    int StartIndex,
    int TotalMessages,
    string? NextCursor,
    long HistoryEpoch,
    int RawMessageCount);

internal sealed class SessionHistoryPageCursor
{
    internal string? SessionId { get; private set; }
    internal long Generation { get; private set; }
    internal long HistoryEpoch { get; private set; }
    internal int StartIndex { get; private set; }
    internal int TotalMessages { get; private set; }
    internal int LoadedPageCount { get; private set; }
    internal string? NextCursor { get; private set; }
    internal bool IsInitialized { get; private set; }
    internal bool CanLoadEarlier => IsInitialized && NextCursor is not null;

    internal void Reset(string sessionId, long generation)
    {
        SessionId = sessionId;
        Generation = generation;
        HistoryEpoch = 0;
        StartIndex = 0;
        TotalMessages = 0;
        LoadedPageCount = 0;
        NextCursor = null;
        IsInitialized = false;
    }

    internal bool Owns(string sessionId, long generation) => IsInitialized
        && string.Equals(SessionId, sessionId, StringComparison.Ordinal)
        && Generation == generation;

    internal bool TrySetNewest(SessionHistoryPage page, string sessionId, long generation)
    {
        if (!IsValidPage(page, sessionId)
            || page.StartIndex + page.RawMessageCount != page.TotalMessages)
        {
            return false;
        }

        SessionId = sessionId;
        Generation = generation;
        HistoryEpoch = page.HistoryEpoch;
        StartIndex = page.StartIndex;
        TotalMessages = page.TotalMessages;
        LoadedPageCount = 1;
        NextCursor = page.NextCursor;
        IsInitialized = true;
        return true;
    }

    internal bool TryPrepend(SessionHistoryPage page, string sessionId, long generation)
    {
        if (!Owns(sessionId, generation)
            || page.HistoryEpoch != HistoryEpoch
            || page.TotalMessages != TotalMessages
            || !IsValidPage(page, sessionId)
            || page.StartIndex >= StartIndex
            || page.StartIndex + page.RawMessageCount != StartIndex)
        {
            return false;
        }

        StartIndex = page.StartIndex;
        NextCursor = page.NextCursor;
        TotalMessages = page.TotalMessages;
        LoadedPageCount++;
        return true;
    }

    private static bool IsValidPage(SessionHistoryPage page, string sessionId) =>
        string.Equals(page.SessionId, sessionId, StringComparison.Ordinal)
        && page.StartIndex >= 0
        && page.TotalMessages >= page.StartIndex
        && page.RawMessageCount >= page.Messages.Count
        && page.RawMessageCount >= 0
        && page.RawMessageCount <= page.TotalMessages - page.StartIndex;
}

internal sealed record SessionRunReplay(
    string RunId,
    IReadOnlyList<AgentActivityItem> Activities,
    IReadOnlyList<ApiService.RunTranscriptEntry> Transcript);

/// <summary>Joins durable messages and run replay by identity, never by tail position.</summary>
internal static class SessionTranscriptBuilder
{
    internal static void BindPersistedLiveTurn(IReadOnlyList<SessionHistoryEntry> history,
        string? runId, ChatMessageItem? user, ChatMessageItem assistant)
    {
        if (string.IsNullOrEmpty(runId)) return;
        var projected = SessionHistoryProjection.Project(history.Select(row => (row.Role, row.Content)));
        for (int index = 0; index < history.Count; index++)
        {
            var row = history[index];
            if (row.SourceRunId != runId || !projected[index].Render) continue;
            var target = row.Role == "assistant" ? assistant : row.Role == "user" ? user : null;
            if (target is null) continue;
            target.SourceRunId = runId;
            target.SourceMessageIndex = row.MessageIndex;
        }
    }

    public static List<ChatMessageItem> Build(
        IReadOnlyList<SessionHistoryEntry> history,
        IReadOnlyList<SessionRunReplay> runs,
        string? sessionId = null,
        bool completeHistory = true)
    {
        var byId = runs.ToDictionary(run => run.RunId, StringComparer.Ordinal);
        var projected = SessionHistoryProjection.Project(history.Select(row => (row.Role, row.Content)));
        var renderedRunIds = history.Select((row, i) => (row, i))
            .Where(pair => pair.row.Role == "assistant" && projected[pair.i].Render)
            .Select(pair => pair.row.SourceRunId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<ChatMessageItem>();
        string? pendingUserRun = null;

        void AppendUnansweredRun()
        {
            if (pendingUserRun is { } id && !renderedRunIds.Contains(id)
                && !applied.Contains(id) && byId.TryGetValue(id, out var replay)
                && (replay.Activities.Count > 0 || replay.Transcript.Count > 0))
            {
                var item = new ChatMessageItem { Role = "assistant", SourceRunId = id };
                ApplyReplay(item, replay);
                messages.Add(item);
                applied.Add(id);
            }
            pendingUserRun = null;
        }

        // Legacy messages have no run ids. Only a balanced history ending in an
        // assistant permits ordinal matching; unmatched runs must not steal an
        // older bubble. Modern/mixed histories always use explicit identities.
        var flags = projected.Select(row => row.Role == "assistant" && row.Render).ToArray();
        var legacy = completeHistory && history.All(row => string.IsNullOrEmpty(row.SourceRunId))
            && flags.Count(flag => flag) == runs.Count
            && history.LastOrDefault()?.Role == "assistant"
            ? ReloadActivitiesPolicy.PlaceReplayedRuns(runs.Count, flags) : null;

        for (int i = 0; i < history.Count; i++)
        {
            var row = history[i];
            if (row.Role == "user")
            {
                AppendUnansweredRun();
                pendingUserRun = row.SourceRunId;
            }
            if (!projected[i].Render) continue;
            var item = new ChatMessageItem
            {
                Role = row.Role, Content = projected[i].Content, SourceRunId = row.SourceRunId,
                SourceMessageIndex = row.MessageIndex ?? i, SessionId = sessionId,
            };
            if (row.Role == "assistant")
            {
                SessionRunReplay? replay = null;
                if (row.SourceRunId is { } id) byId.TryGetValue(id, out replay);
                else if (legacy is not null && legacy[i].ReplayIndex >= 0)
                    replay = runs[legacy[i].ReplayIndex];
                if (replay is not null && applied.Add(replay.RunId))
                {
                    item.SourceRunId = replay.RunId;
                    ApplyReplay(item, replay);
                }
            }
            messages.Add(item);
        }
        if (completeHistory) AppendUnansweredRun();
        return messages;
    }

    private static void ApplyReplay(ChatMessageItem message, SessionRunReplay replay)
    {
        message.AgentActivities = new ObservableCollection<AgentActivityItem>(replay.Activities);
        AgentActivityGrouper.Reconcile(message.AgentActivities);
        var built = AgentReplaySegmentBuilder.Build(replay.Activities, replay.Transcript, message.Content);
        if (built is null) return;
        message.Content = built.Content;
        message.Segments = built.Segments;
        message.ActivitySplitOffsets = built.Offsets;
    }
}
