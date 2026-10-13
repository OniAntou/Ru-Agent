using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ReloadActivitiesPolicyTests
{
    [Fact]
    public void Unanswered_last_user_turn_must_not_claim_the_greeting_run()
    {
        var messages = SessionTranscriptBuilder.Build(
            [new("user", "Xin chào", "hello"), new("assistant", "Chào anh", "hello"),
             new("user", "Giá vàng?", "gold")],
            [new("hello", [], []), new("gold", [], [])]);
        Assert.Equal("hello", messages[1].SourceRunId);
        Assert.Equal("Chào anh", messages[1].Content);
    }

    [Fact]
    public void Live_rows_attach_when_last_assistant_is_final_message()
    {
        Assert.True(ReloadActivitiesPolicy.AttachLiveToLastAssistant(
            historyCount: 2, lastAssistantIndex: 1, liveCount: 2));
    }

    [Fact]
    public void Live_rows_do_not_attach_to_older_assistant()
    {
        // Deny/fail before any assistant text was persisted: history ends with
        // the user message, the only assistant belongs to an older turn.
        Assert.False(ReloadActivitiesPolicy.AttachLiveToLastAssistant(
            historyCount: 3, lastAssistantIndex: 1, liveCount: 2));
    }

    [Fact]
    public void Live_rows_do_not_attach_when_history_has_no_assistant()
    {
        Assert.False(ReloadActivitiesPolicy.AttachLiveToLastAssistant(
            historyCount: 1, lastAssistantIndex: -1, liveCount: 2));
    }

    [Fact]
    public void Orphan_bubble_appended_when_live_rows_unattached()
    {
        Assert.True(ReloadActivitiesPolicy.AppendOrphanedActivities(
            liveCount: 2, liveAttached: false));
    }

    [Fact]
    public void No_orphan_bubble_when_live_rows_attached_or_empty()
    {
        Assert.False(ReloadActivitiesPolicy.AppendOrphanedActivities(
            liveCount: 2, liveAttached: true));
        Assert.False(ReloadActivitiesPolicy.AppendOrphanedActivities(
            liveCount: 0, liveAttached: false));
    }

    [Fact]
    public void Replay_aligns_tail_first_when_counts_match()
    {
        Assert.Equal(0, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 3, totalAssistants: 3, assistantOrdinal: 0));
        Assert.Equal(2, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 3, totalAssistants: 3, assistantOrdinal: 2));
    }

    [Fact]
    public void Legacy_replay_does_not_guess_when_a_turn_has_no_text()
    {
        Assert.Equal(-1, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 4, totalAssistants: 3, assistantOrdinal: 0));
        Assert.Equal(-1, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 4, totalAssistants: 3, assistantOrdinal: 2));
    }

    [Fact]
    public void Replay_returns_missing_when_no_run_backs_the_assistant()
    {
        Assert.Equal(-1, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 0, totalAssistants: 2, assistantOrdinal: 0));
        Assert.Equal(-1, ReloadActivitiesPolicy.ReplayIndexForAssistant(
            runCount: 2, totalAssistants: 3, assistantOrdinal: 0));
    }
}
