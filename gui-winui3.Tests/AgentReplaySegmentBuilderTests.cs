using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A reopened bubble must interleave replayed narration with its tool rows in
/// event order. Without this the transcript falls back to the grouped legacy
/// feed: every row above a text that only held the final step.
/// </summary>
public sealed class AgentReplaySegmentBuilderTests
{
    [Fact]
    public void Build_interleaves_narration_with_activities_and_tracks_offsets()
    {
        var tool = Item("act_tool_1", AgentActivityKind.Terminal);
        var built = AgentReplaySegmentBuilder.Build(
            new[] { tool },
            new[]
            {
                new ApiService.RunTranscriptEntry(10, "text", "checking", null),
                new ApiService.RunTranscriptEntry(20, "activity", null, "act_tool_1"),
                new ApiService.RunTranscriptEntry(30, "text", "done", null),
            });

        Assert.NotNull(built);
        Assert.Equal("checkingdone", built!.Content);
        Assert.Equal(3, built.Segments.Count);
        Assert.IsType<TextSegment>(built.Segments[0]);
        Assert.IsType<ActivitySegment>(built.Segments[1]);
        Assert.IsType<TextSegment>(built.Segments[2]);
        var offset = Assert.Single(built.Offsets);
        Assert.Equal("act_tool_1", offset.ActivityId);
        Assert.Equal("checking".Length, offset.Offset);
    }

    [Fact]
    public void Build_skips_unknown_activity_ids_but_keeps_the_text()
    {
        var built = AgentReplaySegmentBuilder.Build(
            Array.Empty<AgentActivityItem>(),
            new[]
            {
                new ApiService.RunTranscriptEntry(10, "activity", null, "act_missing"),
                new ApiService.RunTranscriptEntry(20, "text", "hello", null),
            });

        Assert.NotNull(built);
        Assert.Equal("hello", built!.Content);
        Assert.Single(built.Segments);
        Assert.Empty(built.Offsets);
    }

    [Fact]
    public void Build_returns_null_without_usable_entries()
    {
        Assert.Null(AgentReplaySegmentBuilder.Build(
            Array.Empty<AgentActivityItem>(),
            Array.Empty<ApiService.RunTranscriptEntry>()));
        Assert.Null(AgentReplaySegmentBuilder.Build(
            Array.Empty<AgentActivityItem>(),
            new[] { new ApiService.RunTranscriptEntry(10, "activity", null, "act_missing") }));
    }

    private static AgentActivityItem Item(string activityId, AgentActivityKind kind) =>
        new(new AgentActivityModel(
            activityId,
            kind,
            AgentActivityState.Completed,
            null, null, null, null, null, null, null, null,
            "succeeded",
            null, null, null, null, null));
}
