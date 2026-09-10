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
}
