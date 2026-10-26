using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionHistoryPagingTests
{
    [Fact]
    public void Older_page_advances_absolute_prefix_and_stale_session_results_are_rejected()
    {
        var state = new SessionHistoryPageCursor();
        state.Reset("session-a", generation: 4);
        Assert.True(state.TrySetNewest(Page("session-a", start: 200, total: 400, cursor: "older-a", epoch: 2),
            "session-a", generation: 4));
        Assert.True(state.TryPrepend(Page("session-a", start: 0, total: 400, cursor: null, epoch: 2),
            "session-a", generation: 4));
        Assert.Equal(0, state.StartIndex);
        Assert.False(state.CanLoadEarlier);

        state.Reset("session-b", generation: 5);
        Assert.True(state.TrySetNewest(Page("session-b", start: 100, total: 300, cursor: "older-b", epoch: 8),
            "session-b", generation: 5));
        Assert.False(state.TryPrepend(Page("session-a", start: 0, total: 400, cursor: null, epoch: 2),
            "session-a", generation: 4));
        Assert.Equal("session-b", state.SessionId);
        Assert.Equal(5, state.Generation);
        Assert.Equal(100, state.StartIndex);
    }

    [Fact]
    public void Older_page_from_a_previous_history_epoch_is_rejected()
    {
        var state = new SessionHistoryPageCursor();
        state.Reset("session-a", generation: 1);
        Assert.True(state.TrySetNewest(Page("session-a", start: 200, total: 400, cursor: "older", epoch: 3),
            "session-a", generation: 1));

        Assert.False(state.TryPrepend(Page("session-a", start: 0, total: 400, cursor: null, epoch: 4),
            "session-a", generation: 1));
        Assert.Equal(200, state.StartIndex);
        Assert.Equal("older", state.NextCursor);
    }

    [Fact]
    public void Partial_history_does_not_synthesize_an_assistant_outside_the_loaded_page()
    {
        var replay = new SessionRunReplay("run-a", [], []);

        var rows = SessionTranscriptBuilder.Build(
            [new SessionHistoryEntry("user", "question", "run-a", 90)],
            [replay],
            "session-a",
            completeHistory: false);

        Assert.Single(rows);
        Assert.Equal(90, rows[0].SourceMessageIndex);
    }

    private static SessionHistoryPage Page(
        string sessionId,
        int start,
        int total,
        string? cursor,
        long epoch) => new(
            sessionId,
            Enumerable.Range(0, 200)
                .Select(index => new SessionHistoryEntry("user", "message", null, start + index))
                .ToArray(),
            start,
            total,
            cursor,
            epoch,
            RawMessageCount: 200);
}
