using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiHeaderTests : IDisposable
{
    private readonly IDisposable _levelPin;

    public TuiHeaderTests()
    {
        _levelPin = TuiPalette.PinLevel(TuiColorLevel.None);
    }

    public void Dispose() => _levelPin.Dispose();

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void Compose_HeaderLinesFitTheViewport(int width)
    {
        var lines = TuiHeader.Compose(width, @"D:\LLM", "Kilo Gateway", "gpt-5-codex", "standard", "a2026184", empty: false);

        Assert.All(lines, line => Assert.True(
            TuiText.VisibleWidth(line) <= width,
            $"line exceeds {width}: '{line}'"));
    }

    [Fact]
    public void NonEmpty_header_keeps_four_rows_and_exact_width()
    {
        var lines = TuiHeader.Compose(100, @"D:\LLM", "opencode", "gpt-sol", "standard", "abcdef123456", empty: false);

        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.Equal(100, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Compose_RendersIdentityContextRuleAndHints()
    {
        var lines = TuiHeader.Compose(100, @"D:\LLM", "Kilo Gateway", "gpt-5-codex", "standard", "a2026184", empty: false);

        Assert.Equal(4, lines.Count);
        Assert.Contains("✦ Ru", lines[0], StringComparison.Ordinal);
        Assert.Contains("a2026184", lines[0], StringComparison.Ordinal);
        Assert.Contains("LLM", lines[1], StringComparison.Ordinal);
        Assert.Contains("standard", lines[1], StringComparison.Ordinal);
        Assert.Contains("Kilo Gateway/gpt-5-codex", lines[1], StringComparison.Ordinal);
        Assert.All(lines[2].Trim(), character => Assert.Equal('─', character));
        Assert.Contains("/ commands", lines[3], StringComparison.Ordinal);
        Assert.Contains("ctrl+o expand", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_EmptyStateTeachesTheSpace()
    {
        var lines = TuiHeader.Compose(80, @"D:\LLM", "Kilo Gateway", "gpt-5-codex", "standard", "a2026184", empty: true);

        var text = string.Join("\n", lines);
        Assert.Contains("coding agent", text, StringComparison.Ordinal);
        Assert.Contains("Type a request to start", text, StringComparison.Ordinal);
        Assert.Contains("/help", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/ commands", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.Compose(0, "w", "b", "m", "p", "s", empty: false));
    }

    [Theory]
    [InlineData(@"D:\LLM", 40, "D:/LLM")]
    [InlineData(@"D:\a\bbbbbbbb\cccccccc", 12, "…/cccccccc")]
    public void ShortenWorkspace_KeepsTheLeafVisible(string workspace, int maxCells, string expected)
    {
        Assert.Equal(expected, TuiHeader.ShortenWorkspace(workspace, maxCells));
    }

    [Fact]
    public void ShortenWorkspace_TruncatesWhenASingleSegmentIsTooLong()
    {
        var shortened = TuiHeader.ShortenWorkspace("/very/long/single-segment-name-here", 10);

        Assert.True(TuiText.VisibleWidth(shortened) <= 10);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void ComposeLandingScreen_FitsViewportWidthWithWavesTitleAndPill(int width)
    {
        var lines = TuiHeader.ComposeLandingScreen(width, @"D:\LLM", "Kilo Gateway", "gpt-5-codex", "standard");

        var text = string.Join("\n", lines);
        Assert.Contains("R u  A g e n t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RUAGENT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System Overview", text, StringComparison.Ordinal);
        Assert.Contains("Kilo Gateway", text, StringComparison.Ordinal);
        Assert.Contains("gpt-5-codex", text, StringComparison.Ordinal);
        Assert.Contains("/help", text, StringComparison.Ordinal);

        Assert.All(lines, l => Assert.True(TuiText.VisibleWidth(l) <= width));
    }

    [Fact]
    public void PaintPill_BrightensEngineAndPreset()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var pill = TuiHeader.PaintPill("◉ models.dev:opencode/m ▾  /  ⎇ standard", "models.dev:opencode/m");

            Assert.Contains(TuiPalette.Code(TuiToken.Bright), pill, StringComparison.Ordinal);
            Assert.Contains("standard", TuiText.Strip(pill), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComposeStarfield_IsStableAndFits()
    {
        var first = TuiHeader.ComposeStarfield(80);
        var second = TuiHeader.ComposeStarfield(80);

        Assert.Equal(TuiHeader.StarfieldRows, first.Count);
        Assert.Equal(first, second);
        Assert.All(first, l => Assert.Equal(80, TuiText.VisibleWidth(l)));
        Assert.Contains(first, l => TuiText.Strip(l).Trim().Length > 0);
    }

    [Fact]
    public void Starfield_UsesUniformFullScreenDensity()
    {
        var field = TuiHeader.ComposeStarfield(160);

        static int Stars(string row) => TuiText.Strip(row).Count(c => !char.IsWhiteSpace(c));
        var counts = field.Select(Stars).ToList();
        var total = counts.Sum();
        var cells = 160 * TuiHeader.StarfieldRows;
        Assert.True(total > cells / 200, "background must carry visible stars");
        Assert.True(total < cells / 6, "background must stay sparse");
        Assert.All(counts, c => Assert.True(c < 160 / 4, "no row may clump"));
    }

    [Fact]
    public void StarEpoch_ReshufflesTheSkyButStaysStablePerEpoch()
    {
        var baseSky = TuiHeader.ComposeStarfield(80);
        var sameSky = TuiHeader.ComposeStarfield(80, epoch: 0);
        var nextSky = TuiHeader.ComposeStarfield(80, epoch: 1);

        Assert.Equal(baseSky, sameSky);
        Assert.NotEqual(
            string.Join("\n", baseSky.Select(TuiText.Strip)),
            string.Join("\n", nextSky.Select(TuiText.Strip)));
        Assert.All(nextSky, l => Assert.Equal(80, TuiText.VisibleWidth(l)));
    }

    [Fact]
    public void ComposeLandingScreen_EpochKeepsWidthAndContent()
    {
        var first = TuiHeader.ComposeLandingScreen(80, @"D:\LLM", "b", "m", "standard", 60, epoch: 0);
        var second = TuiHeader.ComposeLandingScreen(80, @"D:\LLM", "b", "m", "standard", 60, epoch: 7);

        Assert.Equal(first.Count, second.Count);
        Assert.All(second, l => Assert.True(TuiText.VisibleWidth(l) <= 80));
        Assert.Contains("R u  A g e n t", string.Join("\n", second), StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeLandingScreen_CentersVerticallyWithRoomToSpare()
    {
        var flat = TuiHeader.ComposeLandingScreen(80, @"D:\LLM", "b", "m", "standard");
        var centered = TuiHeader.ComposeLandingScreen(80, @"D:\LLM", "b", "m", "standard", 60);

        Assert.True(centered.Count > flat.Count);
        Assert.All(centered, l => Assert.True(TuiText.VisibleWidth(l) <= 80));
    }

    [Fact]
    public void ComposeLandingScreen_ShowsSideFiguresWhenWide()
    {
        var lines = TuiHeader.ComposeLandingScreen(160, @"D:\LLM", "b", "m", "standard");

        var text = string.Join("\n", lines);
        Assert.Contains("R u  A g e n t", text, StringComparison.Ordinal);
        Assert.Contains("⠸", text, StringComparison.Ordinal);
        Assert.All(lines, l => Assert.True(TuiText.VisibleWidth(l) <= 160));
    }

    [Fact]
    public void ComposeLandingScreen_HidesSideFiguresWhenNarrow()
    {
        var lines = TuiHeader.ComposeLandingScreen(80, @"D:\LLM", "b", "m", "standard");

        var text = string.Join("\n", lines);
        Assert.Contains("R u  A g e n t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("⠸", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySideWidths_RequiresRoomForArtAndText()
    {
        Assert.True(TuiHeader.TrySideWidths(160, out _, out _, out var middle));
        Assert.True(middle >= TuiHeader.MinSideMiddleWidth);
        Assert.False(TuiHeader.TrySideWidths(80, out _, out _, out _));
    }

    [Fact]
    public void ComposeLandingScreen_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.ComposeLandingScreen(0, "w", "b", "m", "p"));
    }
}
