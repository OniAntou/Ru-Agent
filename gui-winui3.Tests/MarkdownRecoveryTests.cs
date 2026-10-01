using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Old sessions persisted with newlines folded to spaces, so a whole reply can
/// arrive as one overlong line. The recovery split must break only that wall,
/// and leave normal prose, code talk and short lines exactly alone.
/// </summary>
public sealed class MarkdownRecoveryTests
{
    [Fact]
    public void Short_lines_pass_through_untouched()
    {
        const string line = "Em nói read chỉ trả về 40 byte đầu AGENTS.md.";
        Assert.Equal(new[] { line }, MarkdownRecovery.SplitLine(line));
    }

    [Fact]
    public void Long_lines_without_headings_pass_through_untouched()
    {
        var line = new string('a', MarkdownRecovery.MaxIntactLineLength + 50);
        Assert.Equal(new[] { line }, MarkdownRecovery.SplitLine(line));
    }

    [Fact]
    public void Long_lines_split_before_numbered_headings_with_markers_kept()
    {
        var intro = new string('x', MarkdownRecovery.MaxIntactLineLength);
        var line = intro + " ## 1. bash khó chịu ## 2. số 41 bịa";
        var pieces = MarkdownRecovery.SplitLine(line);

        Assert.Equal(3, pieces.Count);
        Assert.Equal(intro, pieces[0]);
        Assert.Equal("## 1. bash khó chịu", pieces[1]);
        Assert.Equal("## 2. số 41 bịa", pieces[2]);
    }

    [Fact]
    public void Csharp_prose_is_not_split()
    {
        var line = new string('y', MarkdownRecovery.MaxIntactLineLength) + " học C# 8 đi nha";
        Assert.Equal(new[] { line }, MarkdownRecovery.SplitLine(line));
    }

    [Fact]
    public void Empty_lines_stay_empty()
    {
        Assert.Equal(new[] { string.Empty }, MarkdownRecovery.SplitLine(string.Empty));
    }
}
