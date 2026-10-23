using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Regression cover for the single text engine: the measurements both sinks rely on, and
/// the two inputs that used to throw from inside the paint path.
/// </summary>
public sealed class TuiTextEngineTests
{
    [Theory]
    [InlineData("\U0001F680", 2)] // rocket
    [InlineData("\u2705", 2)]    // check mark
    [InlineData("\u4E2D", 2)]    // CJK
    [InlineData("a", 1)]
    public void Common_wide_runes_measure_two_cells(string text, int expected)
    {
        Assert.Equal(expected, TuiText.VisibleWidth(text));
    }

    [Fact]
    public void Tab_advances_to_the_shared_stop()
    {
        Assert.Equal(9, TuiText.VisibleWidth("a\tb"));
    }

    [Fact]
    public void Unpaired_surrogates_never_throw_from_measurement()
    {
        var malformed = "abc" + (char)0xD83D + " def";

        Assert.Equal(8, TuiText.VisibleWidth(malformed));
        Assert.True(TuiText.Truncate(malformed, 5).Contains('\uFFFD', StringComparison.Ordinal));
        Assert.Equal(12, TuiText.VisibleWidth(TuiText.Pad(malformed, 12)));
    }

    [Fact]
    public void MiddleTruncate_never_exceeds_the_requested_width()
    {
        Assert.Equal(2, TuiText.VisibleWidth(TuiText.MiddleTruncate("abcdef", 2)));
        Assert.Equal(3, TuiText.VisibleWidth(TuiText.MiddleTruncate("abcdef", 3)));
    }

    [Fact]
    public void HardWrap_keeps_escape_sequences_whole()
    {
        var rows = TuiText.HardWrapLine("\u001b[31mabc\u001b[0m", 2);

        Assert.Equal(new[] { "\u001b[31mab", "c\u001b[0m" }, rows);
    }

    [Theory]
    [InlineData("\u001b[38;5;300mX")]
    [InlineData("\u001b[38;5;-1mX")]
    [InlineData("\u001b[38;2;999;0;0mX")]
    [InlineData("\u001b[48;5;300mX")]
    public void Out_of_range_sgr_is_clamped_not_thrown(string painted)
    {
        var (clean, _) = TgAnsiPaint.Parse(painted);

        Assert.Equal("X", clean);
    }
}
