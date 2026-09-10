using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ComposerBoxTests
{
    public ComposerBoxTests()
    {
        TuiPalette.Level = TuiColorLevel.None;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void Compose_EveryLineFillsExactlyTheWidth(int width)
    {
        var lines = ComposerBox.Compose(
            width,
            ComposerState.Busy,
            "❯ ",
            "fix the busy lock",
            " in the harness",
            "kilo/gpt-5-codex · high",
            "esc cancel · ctrl+o expand",
            "⠹ thinking · 2.1s");

        Assert.Equal(ComposerBox.Height, lines.Count);
        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Compose_RendersPromptBufferGhostLegendAndHint()
    {
        var lines = ComposerBox.Compose(
            80,
            ComposerState.Idle,
            "❯ ",
            "fix the",
            " busy lock",
            "standard",
            "type / for commands",
            null);

        Assert.StartsWith("╭", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("│ ❯ fix the busy lock", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(" │", lines[1], StringComparison.Ordinal);
        Assert.Contains("standard", lines[2], StringComparison.Ordinal);
        Assert.Contains("type / for commands", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("╯", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ShowsTheTopLabelOnlyWhenGiven()
    {
        var without = ComposerBox.Compose(60, ComposerState.Idle, "❯ ", "", "", "", "", null);
        Assert.DoesNotContain("thinking", without[0], StringComparison.Ordinal);

        var with = ComposerBox.Compose(60, ComposerState.Busy, "❯ ", "", "", "", "", "⠹ thinking · 2.1s");
        Assert.Contains("thinking", with[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_KeepsTheTailOfALongPrompt()
    {
        var longText = new string('a', 200) + "TAIL";
        var lines = ComposerBox.Compose(40, ComposerState.Idle, "❯ ", longText, "", "", "", null);

        Assert.Contains("TAIL", lines[1], StringComparison.Ordinal);
        Assert.Contains("…", lines[1], StringComparison.Ordinal);
        Assert.Equal(40, TuiText.VisibleWidth(lines[1]));
    }

    [Fact]
    public void Compose_ShedsTheHintBeforeBreakingTheFrame()
    {
        var lines = ComposerBox.Compose(
            30,
            ComposerState.Idle,
            "❯ ",
            string.Empty,
            string.Empty,
            "a-very-long-legend-value",
            "a-very-long-hint-value",
            null);

        Assert.Equal(30, TuiText.VisibleWidth(lines[2]));
        Assert.EndsWith("╯", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_HandlesWidthsBelowTheMinimum()
    {
        var lines = ComposerBox.Compose(1, ComposerState.Idle, "❯ ", string.Empty, string.Empty, string.Empty, string.Empty, null);

        Assert.Equal(ComposerBox.MinWidth, TuiText.VisibleWidth(lines[0]));
    }

    [Theory]
    [InlineData(ComposerState.Idle, TuiToken.Line)]
    [InlineData(ComposerState.Slash, TuiToken.Brand)]
    [InlineData(ComposerState.Busy, TuiToken.Brand)]
    [InlineData(ComposerState.Queued, TuiToken.Brand)]
    [InlineData(ComposerState.Approval, TuiToken.Warn)]
    [InlineData(ComposerState.Error, TuiToken.Err)]
    public void TokenFor_MapsEveryStateToItsBorderColour(ComposerState state, TuiToken expected)
    {
        Assert.Equal(expected, ComposerBox.TokenFor(state));
    }

    [Fact]
    public void Compose_PaintsTheBorderWithTheStateTokenWhenColourIsOn()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var lines = ComposerBox.Compose(40, ComposerState.Approval, "❯ ", "", "", "", "", "approval needed");
            Assert.Contains("\u001b[38;2;251;191;36m", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void Compose_EmptyLegendAndHintStillCloses()
    {
        var lines = ComposerBox.Compose(24, ComposerState.Idle, "❯ ", "", "", "", "", null);

        Assert.Equal("╭" + new string('─', 22) + "╮", lines[0]);
        Assert.Equal("╰" + new string('─', 22) + "╯", lines[2]);
    }
}
