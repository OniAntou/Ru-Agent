using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiHeaderTests
{
    public TuiHeaderTests()
    {
        TuiPalette.Level = TuiColorLevel.None;
    }

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
}
