using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The replayed transcript must interleave narration with tool rows in event
/// order: grouping every row above the text is what a reopened session showed
/// before the transcript existed.
/// </summary>
public sealed class RunTranscriptPlannerTests
{
    [Fact]
    public void Merge_interleaves_chunks_with_activities_in_sequence_order()
    {
        var merged = RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)>
            {
                (10, "act_thinking_1"),
                (20, "act_tool_1"),
                (40, "act_tool_2"),
            },
            new List<TranscriptChunk>
            {
                new(15, "checking the file"),
                new(30, "fixing it now"),
                new(50, "done"),
            });

        Assert.Equal(
            new[] { "activity", "text", "activity", "text", "activity", "text" },
            merged.Select(entry => entry.Kind).ToArray());
        Assert.Equal("checking the file", merged[1].Text);
        Assert.Equal("act_tool_1", merged[2].ActivityId);
        Assert.Equal("done", merged[5].Text);
    }

    [Fact]
    public void Merge_puts_the_activity_row_first_on_a_sequence_tie()
    {
        var merged = RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)> { (10, "act_thinking_1") },
            new List<TranscriptChunk> { new(10, "thinking out loud") });

        Assert.Equal(2, merged.Count);
        Assert.Equal(RunTranscriptPlanner.ActivityKind, merged[0].Kind);
        Assert.Equal(RunTranscriptPlanner.TextKind, merged[1].Kind);
    }

    [Fact]
    public void Merge_sorts_unsorted_inputs()
    {
        var merged = RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)>
            {
                (30, "act_b"),
                (10, "act_a"),
            },
            new List<TranscriptChunk> { new(20, "middle") });

        Assert.Equal(
            new long[] { 10, 20, 30 },
            merged.Select(entry => entry.Sequence).ToArray());
    }

    [Fact]
    public void Merge_allows_empty_sides()
    {
        var onlyText = RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)>(),
            new List<TranscriptChunk> { new(5, "solo") });
        var onlyActivity = RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)> { (5, "act_solo") },
            new List<TranscriptChunk>());

        Assert.Single(onlyText);
        Assert.Single(onlyActivity);
        Assert.Empty(RunTranscriptPlanner.Merge(
            new List<(long Sequence, string ActivityId)>(),
            new List<TranscriptChunk>()));
    }
}
