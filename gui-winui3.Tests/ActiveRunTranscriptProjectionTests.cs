using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ActiveRunTranscriptProjectionTests
{
    [Fact]
    public void Adopted_run_promotes_its_replayed_bubble_and_reattaches_live_activities()
    {
        var previous = new ChatMessageItem { Role = "assistant", SourceRunId = "run-old" };
        var replayedActivity = Activity("replayed");
        var active = new ChatMessageItem
        {
            Role = "assistant",
            SessionId = "session-a",
            SourceRunId = "run-active",
            AgentActivities = new() { replayedActivity },
        };
        var messages = new List<ChatMessageItem> { previous, active };
        var feed = new AgentActivityFeedPresenter();
        feed.RegisterReplayedMessages(messages);
        var liveActivities = new ObservableCollection<AgentActivityItem>();
        feed.Apply(ActivityModel("incoming"), liveActivities);
        feed.Apply(ActivityModel("replayed"), liveActivities);

        var adopted = ActiveRunTranscriptProjection.Adopt(
            messages, liveActivities, "session-a", "run-active");

        Assert.Same(active, adopted);
        Assert.False(previous.IsStreaming);
        Assert.True(active.IsStreaming);
        Assert.Equal("run-active", active.SourceRunId);
        Assert.Same(liveActivities, active.AgentActivities);
        Assert.Equal(new[] { "replayed", "incoming" }, liveActivities.Select(item => item.ActivityId));

        var update = feed.Apply(ActivityModel("replayed") with { Detail = "updated from live frame" }, liveActivities);
        Assert.NotNull(update);
        Assert.False(update.Value.Added);
        Assert.Same(replayedActivity, update.Value.Item);
        Assert.Equal("updated from live frame", replayedActivity.Detail);
        Assert.Equal(2, liveActivities.Count);

        var summary = AgentActivitySummaryProjection.ForMessage(active);
        Assert.Contains("Working", summary.Title, StringComparison.Ordinal);

        active.IsStreaming = false;
        Assert.DoesNotContain("Working", AgentActivitySummaryProjection.ForMessage(active).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Adopted_run_creates_one_live_bubble_only_when_replay_has_no_assistant_row()
    {
        var messages = new List<ChatMessageItem>
        {
            new() { Role = "user", Content = "prompt", SourceRunId = "run-active" },
        };
        var activities = new ObservableCollection<AgentActivityItem>();

        var adopted = ActiveRunTranscriptProjection.Adopt(messages, activities, "session-a", "run-active");

        Assert.Same(adopted, Assert.Single(messages, message => message.Role == "assistant"));
        Assert.True(adopted.IsStreaming);
        Assert.Same(activities, adopted.AgentActivities);
        Assert.Equal("session-a", adopted.SessionId);
        Assert.Equal("run-active", adopted.SourceRunId);

        Assert.Same(adopted, ActiveRunTranscriptProjection.Adopt(
            messages, activities, "session-a", "run-active"));
        Assert.Single(messages, message => message.Role == "assistant");
    }

    private static AgentActivityItem Activity(string id) => new(ActivityModel(id));

    private static AgentActivityModel ActivityModel(string id) => new(
        id,
        AgentActivityKind.Explore,
        AgentActivityState.Completed,
        null, null, null, null, null, null, null, null,
        "succeeded", "Read file", null, null);
}
