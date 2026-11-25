using System;
using System.Linq;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The line a running thinking row shows: the first line of the newest paragraph whose
/// first line is already complete, matching the DeepSeek Harness summary rule.
/// </summary>
public sealed class ReasoningPreviewTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("An unfinished line", "")]
    [InlineData("A completed first line\nUnfinished continuation", "A completed first line")]
    [InlineData("First\nSecond\nThird\n", "First")]
    [InlineData("First\n\nNext unfinished", "First")]
    [InlineData("First\n\nNext complete\nDetails", "Next complete")]
    [InlineData("First\n\n\nNext complete\n", "Next complete")]
    [InlineData("First\n \n\t\nNext complete\nDetails", "Next complete")]
    [InlineData("First\r\n\r\n\r\nNext complete\r\n", "Next complete")]
    [InlineData("First\n\r\n \r\nNext complete\r\n", "Next complete")]
    [InlineData("First\n\n\nNext unfinished", "First")]
    [InlineData("\n\n\nFirst complete\n", "First complete")]
    [InlineData("First\n\n\n", "First")]
    [InlineData("only line so far\n", "only line so far")]
    public void Summary_previews_the_latest_completed_paragraph_first_line(string text, string expected) =>
        Assert.Equal(expected, ReasoningPreview.Summary(text));

    [Fact]
    public void Summary_strips_emphasis_and_control_characters()
    {
        Assert.Equal("Plan B works", ReasoningPreview.Summary("**Plan B** works\nmore"));
        Assert.Equal("a b", ReasoningPreview.Summary("a\tb\nmore"));
    }

    [Fact]
    public void Summary_bounds_the_line_and_never_splits_a_surrogate_pair()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 300));
        var line = ReasoningPreview.Summary(emoji + "\nnext");

        Assert.True(line.Length <= ReasoningPreview.MaximumCharacters);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(line[^2]));
    }
}
