using Markdig.Syntax;
using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The chat code card folds long finished blocks, numbers lines only while they
/// don't wrap, and never folds a fence that is still streaming.
/// </summary>
public sealed class CodeCardLayoutTests
{
    [Theory]
    [InlineData(30, true, false)]
    [InlineData(31, true, true)]
    [InlineData(500, false, false)]
    [InlineData(1, true, false)]
    public void Only_a_long_finished_block_folds(int lines, bool complete, bool folds) =>
        Assert.Equal(folds, CodeCardLayout.Folds(lines, complete));

    [Fact]
    public void A_folded_block_keeps_fewer_lines_than_the_fold_threshold() =>
        Assert.True(CodeCardLayout.FoldedLines < CodeCardLayout.FoldAboveLines);

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Line_numbers_show_only_while_lines_do_not_wrap(bool requested, bool wrap, bool shows) =>
        Assert.Equal(shows, CodeCardLayout.ShowsLineNumbers(requested, wrap));

    [Fact]
    public void The_gutter_numbers_every_shown_line()
    {
        Assert.Equal("1\n2\n3", CodeCardLayout.Gutter(3));
        Assert.Equal(string.Empty, CodeCardLayout.Gutter(0));
    }

    [Fact]
    public void The_fold_footer_names_the_whole_length()
    {
        Assert.Equal("Show all 1,204 lines", CodeCardLayout.FoldLabel(1204, expanded: false));
        Assert.Equal("Show fewer lines", CodeCardLayout.FoldLabel(1204, expanded: true));
    }

    [Theory]
    [InlineData("```python\nprint(1)\n```", true)]
    [InlineData("```python\nprint(1)\n", false)]
    public void A_fence_is_complete_only_once_it_closes(string markdown, bool complete)
    {
        // The card reads completeness from Markdig's closing fence; pin that shape.
        var fence = Assert.Single(MarkdownEngine.Parse(markdown).OfType<FencedCodeBlock>());
        Assert.Equal(complete, fence.ClosingFencedCharCount > 0);
    }

    [Fact]
    public void Folded_lines_come_from_the_whole_highlight()
    {
        // A block comment opened above the fold keeps its kind on every shown line.
        string source = "/*\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => "line " + i)) + "\n*/";
        var lines = CodeHighlighter.HighlightLines(source, "csharp");
        Assert.True(CodeCardLayout.Folds(lines.Count, complete: true));
        Assert.All(lines.Take(CodeCardLayout.FoldedLines).SelectMany(line => line),
            span => Assert.Equal(CodeHighlighter.SpanKind.Comment, span.Kind));
    }
}
