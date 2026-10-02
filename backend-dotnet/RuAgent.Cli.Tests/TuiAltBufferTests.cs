using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiAltBufferTests
{
    [Fact]
    public void ShouldUseAltBuffer_RedirectedOutput_ReturnsFalse()
    {
        Assert.False(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: true, term: "xterm-256color", noColor: null,
            columns: 120, rows: 40, classic: false, json: false));
    }

    [Fact]
    public void ShouldUseAltBuffer_SmallTerminal_ReturnsFalse()
    {
        Assert.False(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: false, term: "xterm-256color", noColor: null,
            columns: 79, rows: 40, classic: false, json: false));
    }

    [Fact]
    public void ShouldUseAltBuffer_LiveTerminal_ReturnsTrue()
    {
        Assert.True(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: false, term: "xterm-256color", noColor: null,
            columns: 120, rows: 40, classic: false, json: false));
    }

    [Theory]
    [InlineData(true, "xterm-256color", null, 120, 40, false, false)] // stdout redirected
    [InlineData(false, "xterm-256color", null, 120, 40, true, false)] // --classic
    [InlineData(false, "xterm-256color", null, 120, 40, false, true)] // --json
    [InlineData(false, "xterm-256color", "1", 120, 40, false, false)] // NO_COLOR set
    [InlineData(false, "xterm-256color", "", 120, 40, false, false)] // NO_COLOR defined empty
    [InlineData(false, "dumb", null, 120, 40, false, false)] // TERM=dumb
    [InlineData(false, "DUMB", null, 120, 40, false, false)] // TERM match is case-insensitive
    [InlineData(false, "xterm-256color", null, 79, 40, false, false)] // too narrow
    [InlineData(false, "xterm-256color", null, 120, 23, false, false)] // too short
    public void ShouldUseAltBuffer_FallbackSignals_ReturnFalse(
        bool redirected, string? term, string? noColor, int columns, int rows, bool classic, bool json)
    {
        Assert.False(TuiAltBuffer.ShouldUseAltBuffer(redirected, term, noColor, columns, rows, classic, json));
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void ShouldUseAltBuffer_AtOrAboveTheMinimum_ReturnsTrue(int columns, int rows)
    {
        Assert.True(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: false, term: "xterm-256color", noColor: null,
            columns: columns, rows: rows, classic: false, json: false));
    }
}
