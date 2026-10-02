using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ComposerBoxTests : IDisposable
{
    private readonly IDisposable _levelPin;

    public ComposerBoxTests()
    {
        _levelPin = TuiPalette.PinLevel(TuiColorLevel.None);
    }

    public void Dispose() => _levelPin.Dispose();

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
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var lines = ComposerBox.Compose(40, ComposerState.Approval, "❯ ", "", "", "", "", "approval needed");
            Assert.Contains("\u001b[38;2;245;167;66m", lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_EmptyLegendAndHintStillCloses()
    {
        var lines = ComposerBox.Compose(24, ComposerState.Idle, "❯ ", "", "", "", "", null);

        Assert.Equal("╭" + new string('─', 22) + "╮", lines[0]);
        Assert.Equal("╰" + new string('─', 22) + "╯", lines[2]);
    }

    [Fact]
    public void Compose_PaintsBufferBrightAndGhostMeta()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var lines = ComposerBox.Compose(40, ComposerState.Idle, "❯ ", "hi", "ghost", "", "", null);

            Assert.Contains("\u001b[38;2;238;238;238mhi", lines[1], StringComparison.Ordinal);
            Assert.Contains("\u001b[38;2;128;128;128mghost", lines[1], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void ComposeStack_EveryRowFillsExactlyTheWidth(int width)
    {
        var rows = ComposerBox.ComposeStack(
            width, ComposerState.Idle, "fix the busy lock", string.Empty, "Ru", "gpt-5-codex", "OpenAI");

        Assert.Equal(ComposerBox.StackHeight, rows.Count);
        Assert.All(rows, row => Assert.Equal(width, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void ComposeStack_MetaRowCarriesAgentModelAndProvider()
    {
        var rows = ComposerBox.ComposeStack(
            80, ComposerState.Idle, string.Empty, string.Empty, "Ru", "gpt-5-codex", "OpenAI");

        Assert.StartsWith("╭", rows[0], StringComparison.Ordinal);
        Assert.EndsWith("╯", rows[^1], StringComparison.Ordinal);
        Assert.Contains("Ru", rows[2], StringComparison.Ordinal);
        Assert.Contains("gpt-5-codex", rows[2], StringComparison.Ordinal);
        Assert.Contains("OpenAI", rows[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeHints_PinsKeyOrderAndSpinnerStates()
    {
        var idle = TuiText.Strip(ComposerBox.ComposeHints(100, null, "D:/LLM"));

        Assert.Contains("D:/LLM", idle, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupt", idle, StringComparison.Ordinal);
        Assert.True(idle.IndexOf("/ commands", StringComparison.Ordinal) < idle.IndexOf("tab complete", StringComparison.Ordinal));
        Assert.True(idle.IndexOf("tab complete", StringComparison.Ordinal) < idle.IndexOf("ctrl+o expand", StringComparison.Ordinal));
        Assert.Equal(100, TuiText.VisibleWidth(idle));

        var busy = TuiText.Strip(ComposerBox.ComposeHints(100, "⠋", "D:/LLM"));
        Assert.Contains("⠋ esc interrupt", busy, StringComparison.Ordinal);
        Assert.DoesNotContain("D:/LLM", busy, StringComparison.Ordinal);
    }
}
