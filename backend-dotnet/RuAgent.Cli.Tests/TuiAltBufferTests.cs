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
}
