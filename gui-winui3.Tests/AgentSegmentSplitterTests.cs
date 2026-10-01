using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Splitting live prose mid-sentence leaves a hard joint across two bubbles,
/// so the freeze point snaps back to the previous newline.
/// </summary>
public sealed class AgentSegmentSplitterTests
{
    [Fact]
    public void Split_snaps_back_to_the_previous_newline()
    {
        Assert.Equal(11, AgentSegmentSplitter.SnapToLineStart("first line\nsecond half", 0));
    }

    [Fact]
    public void Split_past_the_last_newline_keeps_the_end()
    {
        Assert.Equal(22, AgentSegmentSplitter.SnapToLineStart("first line\nsecond half", 11));
    }

    [Fact]
    public void Split_without_a_newline_keeps_the_end()
    {
        Assert.Equal(10, AgentSegmentSplitter.SnapToLineStart("no newline", 0));
    }

    [Fact]
    public void Split_clamps_out_of_range_frozen_values()
    {
        Assert.Equal(0, AgentSegmentSplitter.SnapToLineStart(string.Empty, 0));
        Assert.Equal(3, AgentSegmentSplitter.SnapToLineStart("abc", 99));
        Assert.Equal(3, AgentSegmentSplitter.SnapToLineStart("abc", -5));
    }
}
