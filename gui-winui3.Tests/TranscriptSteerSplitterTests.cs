using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>A steer reads as an ordinary message between two replies (ADR 0056).</summary>
public sealed class TranscriptSteerSplitterTests
{
    [Fact]
    public void A_replayed_run_splits_into_reply_message_reply_around_each_steer()
    {
        var narration = new TextSegment("Reading the parser.");
        var read = Activity("act_read", AgentActivityKind.Explore);
        var steer = Steer("act_" + new string('5', 32) + "_steer", "also update the changelog");
        var edit = Activity("act_edit", AgentActivityKind.Edit);
        var answer = new TextSegment("Parser and changelog updated.");
        var message = new ChatMessageItem
        {
            Role = "assistant", SourceRunId = "run_1", SessionId = "s", SourceMessageIndex = 3,
            Segments = new ObservableCollection<AssistantSegment>
            {
                narration, new ActivitySegment(read.ActivityId, read), new ActivitySegment(steer.ActivityId, steer),
                new ActivitySegment(edit.ActivityId, edit), answer,
            },
        };

        var shown = TranscriptSteerSplitter.Split(message);

        Assert.Equal(3, shown.Count);
        Assert.Equal("assistant", shown[0].Role);
        Assert.Equal("Reading the parser.", shown[0].Content);
        Assert.Equal(new[] { read }, shown[0].AgentActivities);
        Assert.Equal("user", shown[1].Role);
        Assert.Equal("also update the changelog", shown[1].Content);
        Assert.Same(message, shown[2]);
        Assert.Equal("Parser and changelog updated.", shown[2].Content);
        Assert.Equal(new[] { edit }, shown[2].AgentActivities);
        Assert.DoesNotContain(shown[2].Segments!, segment => segment is ActivitySegment { IsSteer: true });
        Assert.All(new[] { shown[0], shown[2] }, part => Assert.Equal("run_1", part.SourceRunId));
    }

    [Fact]
    public void A_steer_read_before_any_work_leaves_no_empty_reply_above_it()
    {
        var steer = Steer("act_" + new string('6', 32) + "_steer", "hi");
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Segments = new ObservableCollection<AssistantSegment> { new ActivitySegment(steer.ActivityId, steer), new TextSegment("Hello.") },
        };

        var shown = TranscriptSteerSplitter.Split(message);

        Assert.Equal(new[] { "user", "assistant" }, shown.Select(item => item.Role));
        Assert.Equal("Hello.", shown[1].Content);
    }

    [Fact]
    public void A_reply_without_a_steer_is_left_as_it_is()
    {
        var segments = new ObservableCollection<AssistantSegment> { new TextSegment("Done.") };
        var message = new ChatMessageItem { Role = "assistant", Content = "Done.", Segments = segments };

        var shown = TranscriptSteerSplitter.Split(message);

        Assert.Same(message, Assert.Single(shown));
        Assert.Same(segments, message.Segments);
    }

    private static AgentActivityItem Activity(string id, AgentActivityKind kind) =>
        new(new AgentActivityModel(id, kind, AgentActivityState.Completed, null, null, null, null, null, null, null, null,
            "succeeded", null, null, null));

    private static AgentActivityItem Steer(string id, string text) =>
        new(new AgentActivityModel(id, AgentActivityKind.Steer, AgentActivityState.Completed, null, null, null, null, null,
            null, null, null, "succeeded", null, null, null, Detail: text));
}
