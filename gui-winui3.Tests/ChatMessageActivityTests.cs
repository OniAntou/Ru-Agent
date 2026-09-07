using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatMessageActivityTests
{
    [Fact]
    public void Assistant_activity_anchor_tracks_collection_changes()
    {
        var activities = new ObservableCollection<AgentActivityItem>();
        var message = new ChatMessageItem { AgentActivities = activities };

        Assert.False(message.HasAgentActivities);

        activities.Add(new AgentActivityItem(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            AgentActivityKind.Thinking,
            AgentActivityState.Completed,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            5000,
            "succeeded",
            null,
            null,
            1)));

        Assert.True(message.HasAgentActivities);

        activities.Clear();
        Assert.False(message.HasAgentActivities);
    }
}
