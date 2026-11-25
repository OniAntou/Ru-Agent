using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class TurnProcessHeaderTests
{
    [Fact]
    public void Completed_turn_names_the_elapsed_time()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Content = "done",
            Timestamp = DateTime.Now.AddSeconds(-61),
            IsStreaming = true,
            AgentActivities = new ObservableCollection<AgentActivityItem> { Activity() },
        };

        message.IsStreaming = false;

        Assert.True(message.ShowProcessHeader);
        Assert.StartsWith("Worked for 1m ", message.ProcessHeader);
        Assert.Contains("s", message.ProcessHeader);
        // A finished turn recaps its work before the step count.
        Assert.Equal("Listed files · 1 step", message.ProcessMeta);
        Assert.Equal("\uE8A5", message.ProcessGlyph);
        Assert.False(message.ProcessIsFailed);
    }

    [Fact]
    public void Failed_turn_keeps_the_duration_in_its_header()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Content = "failed",
            Timestamp = DateTime.Now.AddSeconds(-5),
            IsStreaming = true,
            IsError = true,
            AgentActivities = new ObservableCollection<AgentActivityItem> { Activity() },
        };

        message.IsStreaming = false;

        Assert.StartsWith("Took 5s", message.ProcessHeader);
        Assert.Contains("failed", message.ProcessHeader);
    }

    [Fact]
    public void Compact_history_without_an_end_time_still_gets_a_header()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Content = "old answer",
            CompactAgentActivity = true,
            AgentActivities = new ObservableCollection<AgentActivityItem> { Activity() },
        };

        Assert.True(message.ShowProcessHeader);
        Assert.Equal("Worked", message.ProcessHeader);
        Assert.Equal("1m 5s", ChatMessageItem.FormatDuration(TimeSpan.FromSeconds(65)));
        Assert.Equal("1h 2m", ChatMessageItem.FormatDuration(TimeSpan.FromMinutes(62)));
    }

    private static AgentActivityItem Activity() => new(new(
        "activity", AgentActivityKind.Explore, AgentActivityState.Completed,
        null, null, null, null, null, null, null, null, "succeeded", null, null, null));
}
