using System.Collections.ObjectModel;
using System.ComponentModel;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The transcript bubble renders its activity rows through one of two controls, and
/// which one is live depends on <c>HasSegments</c>. That property is derived from the
/// segment collection's count, so it has to follow the collection and not only the
/// reference to it.
/// </summary>
public sealed class ChatMessageItemSegmentTests
{
    private static ChatMessageItem Message() => new()
    {
        Role = "assistant",
        Content = string.Empty,
        IsStreaming = true,
    };

    private static List<string?> Watch(ChatMessageItem message)
    {
        var raised = new List<string?>();
        message.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    [Fact]
    public void A_fresh_message_has_no_segments_and_shows_the_legacy_layout()
    {
        var message = Message();

        Assert.False(message.HasSegments);
        Assert.False(message.ShowLegacyActivityFeed);
        Assert.True(message.ShowLegacyText);
    }

    [Fact]
    public void Growing_the_segment_collection_announces_that_segments_now_exist()
    {
        // The live turn creates the collection EMPTY and then adds to it, so a
        // notification raised only by the setter would leave HasSegments stuck false
        // for the whole turn: the segments control stays collapsed and the legacy feed
        // stays on screen, which is what empties the bubble mid-turn.
        var message = Message();
        var segments = new ObservableCollection<AssistantSegment>();
        message.Segments = segments;

        var raised = Watch(message);
        segments.Add(new ActivitySegment("act_1", new AgentActivityItem(Activity())));

        Assert.True(message.HasSegments);
        Assert.Contains(nameof(ChatMessageItem.HasSegments), raised);
    }

    [Fact]
    public void Growing_the_collection_switches_the_two_renderers()
    {
        var message = Message();
        var segments = new ObservableCollection<AssistantSegment>();
        message.Segments = segments;

        Assert.False(message.ShowLegacyActivityFeed);

        segments.Add(new ActivitySegment("act_1", new AgentActivityItem(Activity())));

        // Exactly one renderer is live at a time, and it is the segments one now.
        Assert.True(message.ShowLegacyActivityFeed is false);
        Assert.False(message.ShowLegacyText);
    }

    [Fact]
    public void Emptying_the_collection_hands_the_transcript_back_to_the_legacy_layout()
    {
        var message = Message();
        var segments = new ObservableCollection<AssistantSegment>
        {
            new ActivitySegment("act_1", new AgentActivityItem(Activity())),
        };
        message.Segments = segments;

        segments.Clear();

        Assert.False(message.HasSegments);
        Assert.True(message.ShowLegacyText);
    }

    [Fact]
    public void Replacing_the_collection_stops_listening_to_the_old_one()
    {
        // A stale subscription would keep announcing properties for a collection the
        // message no longer renders.
        var message = Message();
        var first = new ObservableCollection<AssistantSegment>();
        var second = new ObservableCollection<AssistantSegment>();
        message.Segments = first;
        message.Segments = second;

        var raised = Watch(message);
        first.Add(new ActivitySegment("act_old", new AgentActivityItem(Activity())));

        Assert.False(message.HasSegments);
        Assert.DoesNotContain(nameof(ChatMessageItem.HasSegments), raised);
    }

    [Fact]
    public void Clearing_the_segment_reference_also_stops_listening()
    {
        var message = Message();
        var segments = new ObservableCollection<AssistantSegment>();
        message.Segments = segments;

        message.Segments = null;
        var raised = Watch(message);
        segments.Add(new ActivitySegment("act_1", new AgentActivityItem(Activity())));

        Assert.False(message.HasSegments);
        Assert.DoesNotContain(nameof(ChatMessageItem.HasSegments), raised);
    }

    [Fact]
    public void Assigning_an_already_populated_collection_announces_it_once()
    {
        // The post-turn rebuild takes this path, which is why the rows appeared only
        // when the turn ended.
        var message = Message();
        var segments = new ObservableCollection<AssistantSegment>
        {
            new ActivitySegment("act_1", new AgentActivityItem(Activity())),
        };

        var raised = Watch(message);
        message.Segments = segments;

        Assert.True(message.HasSegments);
        Assert.Equal(1, raised.Count(name => name == nameof(ChatMessageItem.HasSegments)));
    }

    private static RuAgent.Client.AgentActivityModel Activity() => new(
        "act_1",
        RuAgent.Protocol.Harness.AgentActivityKind.Thinking,
        RuAgent.Protocol.Harness.AgentActivityState.Started,
        FileName: null,
        LineStart: null,
        LineEnd: null,
        Symbol: null,
        Count: null,
        Added: null,
        Removed: null,
        DurationMs: null,
        SafeCode: "running",
        ToolDisplay: null,
        DurableSequence: null,
        TransientSequence: 1);
}
