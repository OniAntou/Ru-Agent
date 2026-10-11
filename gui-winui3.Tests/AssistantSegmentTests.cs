using System.Collections.Generic;
using System.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class AssistantSegmentTests
{
    private static AgentActivityItem Item(string id) => new(new AgentActivityModel(
        ActivityId: id,
        Kind: AgentActivityKind.Thinking,
        State: AgentActivityState.Started,
        FileName: null,
        LineStart: null,
        LineEnd: null,
        Symbol: null,
        Count: null,
        Added: null,
        Removed: null,
        DurationMs: null,
        SafeCode: "in_progress",
        ToolDisplay: null,
        DurableSequence: null,
        TransientSequence: 1));

    [Fact]
    public void BuildSettled_without_activities_returns_single_text()
    {
        var segments = ActivitySegmenter.BuildSettled(
            "hello",
            new List<AgentActivityItem>(),
            new List<ActivitySplitOffset>());

        var text = Assert.Single(segments);
        Assert.Equal("hello", Assert.IsType<TextSegment>(text).Text);
    }

    [Fact]
    public void BuildSettled_keeps_leading_narration_above_later_activities()
    {
        var first = Item("act_11111111111111111111111111111111");
        var second = Item("act_22222222222222222222222222222222");
        var segments = ActivitySegmenter.BuildSettled(
            "Chao anh",
            new List<AgentActivityItem> { first, second },
            new List<ActivitySplitOffset>
            {
                new("act_11111111111111111111111111111111", 8),
                new("act_22222222222222222222222222222222", 8),
            });

        Assert.Equal(3, segments.Count);
        Assert.Equal("Chao anh", Assert.IsType<TextSegment>(segments[0]).Text);
        Assert.Same(first, Assert.IsType<ActivitySegment>(segments[1]).Item);
        Assert.Same(second, Assert.IsType<ActivitySegment>(segments[2]).Item);
    }

    [Fact]
    public void BuildSettled_interleaves_middle_text_between_activities()
    {
        var first = Item("act_11111111111111111111111111111111");
        var second = Item("act_22222222222222222222222222222222");
        var segments = ActivitySegmenter.BuildSettled(
            "AB12CD",
            new List<AgentActivityItem> { first, second },
            new List<ActivitySplitOffset>
            {
                new("act_11111111111111111111111111111111", 2),
                new("act_22222222222222222222222222222222", 4),
            });

        Assert.Equal(5, segments.Count);
        Assert.Equal("AB", Assert.IsType<TextSegment>(segments[0]).Text);
        Assert.Same(first, Assert.IsType<ActivitySegment>(segments[1]).Item);
        Assert.Equal("12", Assert.IsType<TextSegment>(segments[2]).Text);
        Assert.Same(second, Assert.IsType<ActivitySegment>(segments[3]).Item);
        Assert.Equal("CD", Assert.IsType<TextSegment>(segments[4]).Text);
    }

    [Fact]
    public void BuildSettled_missing_offset_falls_back_to_legacy_order()
    {
        var first = Item("act_11111111111111111111111111111111");
        var segments = ActivitySegmenter.BuildSettled(
            "hello",
            new List<AgentActivityItem> { first },
            new List<ActivitySplitOffset>());

        Assert.Equal(2, segments.Count);
        Assert.Same(first, Assert.IsType<ActivitySegment>(segments[0]).Item);
        Assert.Equal("hello", Assert.IsType<TextSegment>(segments[1]).Text);
    }

    [Fact]
    public void BuildSettled_clamps_out_of_range_offsets()
    {
        var first = Item("act_11111111111111111111111111111111");
        var segments = ActivitySegmenter.BuildSettled(
            "hi",
            new List<AgentActivityItem> { first },
            new List<ActivitySplitOffset>
            {
                new("act_11111111111111111111111111111111", 999),
                new("act_11111111111111111111111111111111", -5),
            });

        // First offset wins and clamps to the text end: the activity arrived
        // after all text, so the whole text stays above it — never throws,
        // never loses text.
        Assert.Equal(2, segments.Count);
        Assert.Equal("hi", Assert.IsType<TextSegment>(segments[0]).Text);
        Assert.Same(first, Assert.IsType<ActivitySegment>(segments[1]).Item);
    }

    [Fact]
    public void TextSegment_notifies_on_text_change()
    {
        var segment = new TextSegment("a");
        var changes = new List<string?>();
        segment.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        segment.Text = "ab";

        Assert.Contains("Text", changes);
        Assert.Contains("HasText", changes);
        Assert.True(segment.HasText);
    }

    [Fact]
    public void TextSegment_is_settled_by_default_and_notifies_on_liveness_change()
    {
        var segment = new TextSegment("a");
        Assert.False(segment.IsLive);

        var changes = new List<string?>();
        segment.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        segment.IsLive = true;

        Assert.True(segment.IsLive);
        Assert.Contains("IsLive", changes);
    }

    [Fact]
    public void SettleSegments_ends_live_rendering_for_text_slices_only()
    {
        var live = new TextSegment("streaming") { IsLive = true };
        var settled = new TextSegment("frozen");
        var activity = new ActivitySegment(
            "act_11111111111111111111111111111111",
            Item("act_11111111111111111111111111111111"));
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Segments = new System.Collections.ObjectModel.ObservableCollection<AssistantSegment>
            {
                settled,
                activity,
                live,
            },
        };

        message.SettleSegments();

        Assert.False(live.IsLive);
        Assert.False(settled.IsLive);
        Assert.Same(activity.Item, ((ActivitySegment)message.Segments[1]).Item);
    }

    [Fact]
    public void SettleSegments_tolerates_a_message_without_segments()
    {
        var message = new ChatMessageItem { Role = "assistant" };

        var exception = Record.Exception(() => message.SettleSegments());

        Assert.Null(exception);
    }

    [Fact]
    public void Message_hides_legacy_layout_once_segments_exist()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Content = "hello",
            AgentActivities = new System.Collections.ObjectModel.ObservableCollection<AgentActivityItem>
            {
                Item("act_11111111111111111111111111111111"),
            },
        };

        Assert.True(message.ShowLegacyActivityFeed);
        Assert.True(message.ShowLegacyText);
        Assert.False(message.HasSegments);

        message.Segments = new System.Collections.ObjectModel.ObservableCollection<AssistantSegment>
        {
            new TextSegment("hello"),
        };

        Assert.False(message.ShowLegacyActivityFeed);
        Assert.False(message.ShowLegacyText);
        Assert.True(message.HasSegments);
    }
}
