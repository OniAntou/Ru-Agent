using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ReloadActivitiesPolicyTests
{
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
}
