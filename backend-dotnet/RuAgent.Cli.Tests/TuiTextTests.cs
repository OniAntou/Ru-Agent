using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiTextTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("❯", 1)]
    [InlineData("❯ ❯", 3)]
    [InlineData("─", 1)]
    [InlineData("╭ ╮ ╰ ╯", 7)]
    [InlineData("⠋", 1)]
    [InlineData("✓ ✗ ▲ ◆ ✦ ▸", 11)]
    [InlineData("· ", 2)]
    [InlineData("⇣12.4k", 6)]
    [InlineData("ế", 1)]
    [InlineData("Để em xem", 9)]
    public void VisibleWidth_MeasuresBmpGlyphsAsSingleCell(string text, int expected)
    {
        Assert.Equal(expected, TuiText.VisibleWidth(text));
    }

    [Fact]
    public void VisibleWidth_TreatsCombiningMarksAsZeroWidth()
    {
        Assert.Equal(1, TuiText.VisibleWidth("e\u0301"));
        Assert.Equal(3, TuiText.VisibleWidth("a\u0300bc"));
    }

    [Theory]
    [InlineData("漢", 2)]
    [InlineData("漢字", 4)]
    [InlineData("。", 2)]
    [InlineData("🙂", 2)]
    [InlineData("a🙂b", 4)]
    public void VisibleWidth_TreatsCjkAndEmojiAsDoubleWidth(string text, int expected)
    {
        Assert.Equal(expected, TuiText.VisibleWidth(text));
    }

    [Fact]
    public void VisibleWidth_IgnoresAnsiSequences()
    {
        Assert.Equal(5, TuiText.VisibleWidth("\u001b[38;2;56;189;248mhello\u001b[0m"));
        Assert.Equal(2, TuiText.VisibleWidth("\u001b[1m❯ \u001b[0m"));
    }

    [Fact]
    public void Strip_RemovesAnsiSequences()
    {
        Assert.Equal("hello", TuiText.Strip("\u001b[38;5;39mhello\u001b[0m"));
        Assert.Equal("plain", TuiText.Strip("plain"));
    }

    [Fact]
    public void SanitizeUntrusted_RemovesEscapesControlsAndBidiButKeepsLayout()
    {
        Assert.Equal(
            "hitxt\n\tnext",
            TuiText.SanitizeUntrusted("\u001b[2Jhi\u0007\u202Etxt\n\tnext"));
        Assert.Equal("visible", TuiText.SanitizeUntrusted("\u001b]0;title\u0007visible"));
    }

    [Fact]
    public void ExpandTabs_StartsAtTheGivenColumn()
    {
        Assert.Equal("a       b", TuiText.ExpandTabs("a\tb", 0, out var firstEnd));
        Assert.Equal(9, firstEnd);
        Assert.Equal("a    b", TuiText.ExpandTabs("a\tb", 3, out var secondEnd));
        Assert.Equal(9, secondEnd);
    }

    [Fact]
    public void RuneWidth_ClassifiesControlsAndNegativeInput()
    {
        Assert.Equal(0, TuiText.RuneWidth(0));
        Assert.Equal(0, TuiText.RuneWidth('\n'));
        Assert.Equal(0, TuiText.RuneWidth(127));
        Assert.Equal(1, TuiText.RuneWidth('a'));
    }

    [Fact]
    public void Truncate_ReturnsInputWhenItAlreadyFits()
    {
        Assert.Equal("hello", TuiText.Truncate("hello", 5));
        Assert.Equal("hello", TuiText.Truncate("hello", 9));
    }

    [Fact]
    public void Truncate_ClipsToWidthWithEllipsis()
    {
        var result = TuiText.Truncate("hello world", 8);
        Assert.Equal("hello w…", result);
        Assert.Equal(8, TuiText.VisibleWidth(result));
    }

    [Fact]
    public void Truncate_DoesNotSplitWideRuneAtBoundary()
    {
        var result = TuiText.Truncate("漢漢漢", 5);
        Assert.Equal("漢漢…", result);
        Assert.Equal(5, TuiText.VisibleWidth(result));
    }

    [Fact]
    public void Truncate_ClosesStylingItHadToCut()
    {
        var result = TuiText.Truncate("\u001b[38;2;56;189;248mabcdefghij\u001b[0m", 4);
        Assert.Contains("\u001b[0m", result, StringComparison.Ordinal);
        Assert.Equal(4, TuiText.VisibleWidth(result));
    }

    [Fact]
    public void Truncate_EmptyWhenWidthIsZero()
    {
        Assert.Equal(string.Empty, TuiText.Truncate("hello", 0));
        Assert.Equal(string.Empty, TuiText.Truncate(null, 4));
    }

    [Fact]
    public void Pad_ProducesExactVisibleWidth()
    {
        Assert.Equal("ab   ", TuiText.Pad("ab", 5));
        Assert.Equal("abcd…", TuiText.Pad("abcdefgh", 5));
        Assert.Equal(5, TuiText.VisibleWidth(TuiText.Pad("漢漢漢", 5)));
    }

    [Fact]
    public void Wrap_SplitsOnSpacesWithoutExceedingWidth()
    {
        var lines = TuiText.Wrap("the quick brown fox jumps", 10);
        Assert.Equal(new[] { "the quick", "brown fox", "jumps" }, lines);
        Assert.All(lines, line => Assert.True(TuiText.VisibleWidth(line) <= 10));
    }

    [Fact]
    public void Wrap_HardSplitsWordsWiderThanTheLine()
    {
        var lines = TuiText.Wrap("abcdefghijkl", 5);
        Assert.Equal(new[] { "abcde", "fghij", "kl" }, lines);
    }

    [Fact]
    public void Wrap_KeepsExplicitBlankLines()
    {
        var lines = TuiText.Wrap("a\n\nb", 10);
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }
}
