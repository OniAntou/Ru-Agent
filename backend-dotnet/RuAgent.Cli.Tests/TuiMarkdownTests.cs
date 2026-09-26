using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiMarkdownTests
{
    private static IDisposable Level() => TuiPalette.PinLevel(TuiColorLevel.None);

    [Fact]
    public void BoldMarkersAreStrippedAndEmphasised()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("a **b** c", 80);

        Assert.Equal("a b c", Assert.Single(rows));
    }

    [Fact]
    public void InlineCodeKeepsItsBackticksOut()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("run `dotnet test` now", 80);

        Assert.Equal("run dotnet test now", Assert.Single(rows));
    }

    [Fact]
    public void AFenceContributesNoRowOfItsOwn()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();

        Assert.Empty(markdown.RenderLine("```csharp", 80));
        Assert.True(markdown.InFence);

        var code = Assert.Single(markdown.RenderLine("var x = 1;", 80));
        Assert.Contains("var x = 1;", code, StringComparison.Ordinal);
        Assert.Contains("│", code, StringComparison.Ordinal);

        Assert.Empty(markdown.RenderLine("```", 80));
        Assert.False(markdown.InFence);
    }

    [Fact]
    public void CodeInsideAFenceIsNeverTreatedAsMarkdown()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();
        markdown.RenderLine("```", 80);

        var row = Assert.Single(markdown.RenderLine("if (a ** b) { }", 80));

        Assert.Contains("if (a ** b) { }", row, StringComparison.Ordinal);
    }

    [Fact]
    public void BulletsAndOrderedItemsGetTheirMarkers()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();

        Assert.Equal("  •  first", Assert.Single(markdown.RenderLine("- first", 80)));
        Assert.Equal("  1.  step", Assert.Single(markdown.RenderLine("1. step", 80)));
    }

    [Fact]
    public void AHeadingLosesItsHashesAndIsBold()
    {
        using var _ = Level();

        Assert.Equal("Title", Assert.Single(new TuiMarkdown().RenderLine("## Title", 80)));
    }

    [Fact]
    public void AQuoteKeepsAGutterAndARuleBecomesALine()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();

        Assert.Contains("│", Assert.Single(markdown.RenderLine("> quoted", 80)), StringComparison.Ordinal);
        Assert.Contains("─", Assert.Single(markdown.RenderLine("---", 80)), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkKeepsItsLabelAndDropsTheTarget()
    {
        using var _ = Level();

        Assert.Equal("see docs", Assert.Single(new TuiMarkdown().RenderLine("see [docs](https://example.com)", 80)));
    }

    [Fact]
    public void AnEscapedMarkerIsLiteral()
    {
        using var _ = Level();

        Assert.Equal("2 * 3 = 6", Assert.Single(new TuiMarkdown().RenderLine(@"2 \* 3 = 6", 80)));
    }

    [Fact]
    public void AnUnclosedMarkerStaysLiteralRatherThanLosingText()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();

        Assert.Equal("a **b", Assert.Single(markdown.RenderLine("a **b", 80)));
        Assert.Equal("a `b", Assert.Single(new TuiMarkdown().RenderLine("a `b", 80)));
    }

    [Fact]
    public void ACloserThatStartedInAnEarlierDeltaIsNotAFreshOpener()
    {
        using var _ = Level();

        // The tail of "a **b** c" after the first delta begins on a word, so the leading
        // "**" is the run's closer, not the start of a new bold run.
        Assert.Equal("** c", TuiMarkdown.RenderInline("** c", 80, continuesWord: true));
        Assert.Equal("** c", TuiMarkdown.RenderInline("** c", 80, continuesWord: false));
    }

    [Fact]
    public void TextAfterAFenceIsProseAgain()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();
        markdown.RenderLine("```", 80);
        markdown.RenderLine("x", 80);
        markdown.RenderLine("```", 80);

        Assert.Equal("a b c", Assert.Single(markdown.RenderLine("a **b** c", 80)));
    }

    [Fact]
    public void ResetDropsAFenceLeftOpenByAnInterruptedTurn()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();
        markdown.RenderLine("```", 80);
        Assert.True(markdown.InFence);

        markdown.Reset();

        Assert.False(markdown.InFence);
        Assert.Equal("a b c", Assert.Single(markdown.RenderLine("a **b** c", 80)));
    }

    [Fact]
    public void ALongLineWrapsWithinTheWidth()
    {
        using var _ = Level();
        var markdown = new TuiMarkdown();

        var rows = markdown.RenderLine(string.Join(' ', Enumerable.Repeat("word", 40)), 40);

        Assert.True(rows.Count > 1);
        Assert.All(rows, row => Assert.True(TuiText.VisibleWidth(row) <= 40, $"too wide: {row}"));
    }

    [Fact]
    public void APlainShortLineIsOneRow()
    {
        using var _ = Level();

        Assert.Equal("just text", Assert.Single(new TuiMarkdown().RenderLine("just text", 80)));
    }

    [Theory]
    [InlineData("abc", 0)]
    [InlineData("a **", 2)]
    [InlineData("a `", 1)]
    [InlineData("a *", 1)]
    [InlineData("", 0)]
    public void HoldbackKeepsOnlyTheTrailingMarkerRun(string buffer, int expected)
    {
        Assert.Equal(expected, TuiMarkdown.Holdback(buffer));
    }

    [Fact]
    public void HoldbackDoesNotReachBackToAMarkerThatBelongsToSomethingElse()
    {
        // The underscore in a tool-call wrapper is not a markdown marker; holding from it
        // would swallow the wrapper itself.
        Assert.Equal(0, TuiMarkdown.Holdback("<tool_call>\n"));
    }
}
