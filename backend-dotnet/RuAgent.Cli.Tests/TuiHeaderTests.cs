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
    public void Compose_HeaderLineFitsTheViewport(int width)
    {
        var line = TuiHeader.Compose(width, "Homepage button color change in repo workflow", 39413);

        Assert.True(TuiText.VisibleWidth(line) <= width, $"line exceeds {width}: '{line}'");
    }

    [Fact]
    public void Compose_KeepsExactWidthTitleAndGroupedTally()
    {
        var line = TuiHeader.Compose(100, "Fix broken tests", 39413);

        Assert.Equal(100, TuiText.VisibleWidth(line));
        Assert.StartsWith("# Fix broken tests", line, StringComparison.Ordinal);
        Assert.EndsWith("39,413", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_SingleRowCaptionsTheSessionWithoutTitle()
    {
        var line = TuiHeader.Compose(40, null, null);

        Assert.Equal(40, TuiText.VisibleWidth(line));
        Assert.StartsWith("# session", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DropsTheTallyWhenTheRowCannotFitBoth()
    {
        var line = TuiHeader.Compose(12, "A long session title", 1234567);

        Assert.Equal(12, TuiText.VisibleWidth(line));
        Assert.DoesNotContain("1,234,567", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ShowsContextPercentAndCostInTheCluster()
    {
        var line = TuiHeader.Compose(100, "Fix broken tests", 39413, contextPercent: 20, costUsd: 0.29);

        Assert.Contains("39,413", line, StringComparison.Ordinal);
        Assert.Contains("20%", line, StringComparison.Ordinal);
        Assert.Contains("($0.29)", line, StringComparison.Ordinal);
        Assert.Equal(100, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void Compose_OmitsUnpublishedClusterMembers()
    {
        var line = TuiHeader.Compose(60, "Fix", null, contextPercent: 20);

        Assert.Contains("20%", line, StringComparison.Ordinal);
        Assert.DoesNotContain("($", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.Compose(0, "title", 1));
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
    public void ComposeLandingScreen_FitsTheViewportWithTheWordmark(int width)
    {
        var lines = TuiHeader.ComposeLandingScreen(width);

        var text = string.Join("\n", lines);
        Assert.Contains("█", text, StringComparison.Ordinal);
        Assert.All(lines, l => Assert.True(TuiText.VisibleWidth(l) <= width));
    }

    [Fact]
    public void Wordmark_PaintsRuMutedAndAgentBright()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var lines = TuiHeader.Wordmark(80);

            var joined = string.Join("\n", lines);
            Assert.Contains(TuiPalette.Code(TuiToken.Meta), joined, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Bright), joined, StringComparison.Ordinal);
            Assert.Contains("█", TuiText.Strip(joined), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Wordmark_CentersTheBlockAndKeepsRowCount()
    {
        var lines = TuiHeader.Wordmark(80);

        Assert.Equal(TuiHeader.WordmarkRows + 2, lines.Count);
        Assert.Equal(string.Empty, TuiText.Strip(lines[0]));
        Assert.Equal(string.Empty, TuiText.Strip(lines[^1]));
        Assert.True(TuiText.Strip(lines[1]).Trim().Length > 0);
        Assert.StartsWith(" ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Wordmark_FallsBackToTextWhenNarrow()
    {
        var lines = TuiHeader.Wordmark(30);

        var text = string.Join("\n", lines);
        Assert.Contains("Ru Agent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("█", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeLandingScreen_CentersVerticallyWithRoomToSpare()
    {
        var flat = TuiHeader.ComposeLandingScreen(80);
        var centered = TuiHeader.ComposeLandingScreen(80, viewportRows: 60);

        Assert.True(centered.Count > flat.Count);
        Assert.All(centered, l => Assert.True(TuiText.VisibleWidth(l) <= 80));
    }

    [Fact]
    public void ComposeLandingScreen_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.ComposeLandingScreen(0));
    }
}
