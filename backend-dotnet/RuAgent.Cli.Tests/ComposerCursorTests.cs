using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ComposerCursorTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void CursorColumn_SitsRightAfterTheTypedText(int width)
    {
        var column = ComposerBox.CursorColumn(width, "❯ ", "hello");

        // border + space + prompt + text, as a one-based column.
        Assert.Equal(3 + 2 + 5, column);
    }

    [Fact]
    public void CursorColumn_FollowsTheTailWhenTheBufferOverflows()
    {
        var width = 40;
        var inner = width - 4;
        var available = inner - 2;
        var buffer = new string('x', available + 10);

        var column = ComposerBox.CursorColumn(width, "❯ ", buffer);

        // Only the tail is on screen, so the caret stops at the frame's inner edge.
        Assert.Equal(3 + 2 + TuiText.VisibleWidth(TuiText.Tail(buffer, available)), column);
        Assert.True(column <= width - 1, "caret must stay inside the frame");
    }

    [Fact]
    public void CursorColumn_WithAnEmptyBufferStopsAfterThePrompt()
    {
        Assert.Equal(5, ComposerBox.CursorColumn(80, "❯ ", string.Empty));
    }

    [Fact]
    public void CursorColumn_CountsWideCharactersAsTwoCells()
    {
        var narrow = ComposerBox.CursorColumn(80, "❯ ", "ab");
        var wide = ComposerBox.CursorColumn(80, "❯ ", "中文");

        Assert.Equal(narrow + 2, wide);
    }

    [Fact]
    public void CursorColumn_TreatsCombiningVietnameseMarksAsOneCell()
    {
        var precomposed = ComposerBox.CursorColumn(80, "❯ ", "ỗ");
        var decomposed = ComposerBox.CursorColumn(80, "❯ ", "o\u0302\u0303");

        Assert.Equal(precomposed, decomposed);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(24)]
    public void CursorColumn_NeverEscapesTheFrameOnNarrowTerminals(int width)
    {
        var column = ComposerBox.CursorColumn(width, "❯ ", new string('y', 200));

        Assert.InRange(column, 1, width - 1);
    }

    [Fact]
    public void CursorColumn_ReservesTheLegendForALongBuffer()
    {
        var width = 40;
        var legend = "gpt-5 · high";
        var legendWidth = TuiText.VisibleWidth(legend);
        var buffer = new string('x', 200);

        var column = ComposerBox.CursorColumn(width, "❯ ", buffer, legend);

        // Border+gutter+prompt (5) plus the buffer width left once the legend and its
        // separator space are reserved.
        Assert.Equal(3 + 2 + (width - 4 - legendWidth - 1 - 2), column);
        Assert.True(column <= width - 2 - legendWidth, "caret must stay left of the legend");
    }

    [Fact]
    public void CursorColumn_ShortBufferIsUnchangedByTheLegend()
    {
        var plain = ComposerBox.CursorColumn(80, "❯ ", "hello");
        var withLegend = ComposerBox.CursorColumn(80, "❯ ", "hello", "gpt-5 · high");

        Assert.Equal(plain, withLegend);
    }

    [Fact]
    public void CursorColumn_MatchesTheBufferTailComposeRowShows()
    {
        var width = 40;
        var legend = "standard";
        var buffer = new string('x', 200);
        var available = width - 4 - TuiText.VisibleWidth(legend) - 1 - 2;
        var shown = TuiText.Tail(buffer, available);

        Assert.Equal(
            3 + 2 + TuiText.VisibleWidth(shown),
            ComposerBox.CursorColumn(width, "❯ ", buffer, legend));
    }
}
