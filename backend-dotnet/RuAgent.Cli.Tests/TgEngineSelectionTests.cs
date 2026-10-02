using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgEngineSelectionTests
{
    [Fact]
    public void Resolve_DefaultsToClassic()
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: null, outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, term: "xterm-256color", classic: false));
    }

    [Fact]
    public void Resolve_TerminalGuiOptIn_LiveTerminal_ReturnsTerminalGui()
    {
        Assert.Equal(TuiEngine.TerminalGui, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, term: "xterm-256color", classic: false));
    }

    [Fact]
    public void Resolve_TerminalGuiOptIn_IgnoresCaseAndWhitespace()
    {
        Assert.Equal(TuiEngine.TerminalGui, TuiEngineSelection.Resolve(
            envValue: "  TerminalGUI  ", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, term: "xterm-256color", classic: false));
    }

    [Theory]
    [InlineData(true, false, null, 120, 40, null, false)]     // redirected
    [InlineData(false, true, null, 120, 40, null, false)]     // json
    [InlineData(false, false, "1", 120, 40, null, false)]     // NO_COLOR
    [InlineData(false, false, "", 120, 40, null, false)]      // NO_COLOR set but empty
    [InlineData(false, false, null, 79, 40, null, false)]     // too narrow
    [InlineData(false, false, null, 120, 23, null, false)]    // too short
    [InlineData(false, false, null, 120, 40, null, true)]     // classic flag
    [InlineData(false, false, null, 120, 40, "dumb", false)]  // TERM=dumb
    [InlineData(false, false, null, 120, 40, "DUMB", false)]  // TERM=DUMB
    public void Resolve_TerminalGuiRequested_ButGateFails_ReturnsClassic(
        bool outputRedirected, bool json, string? noColor, int columns, int rows, string? term, bool classic)
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected, json,
            columns, rows, noColor, term, classic));
    }

    [Fact]
    public void Resolve_UnknownValue_ReturnsClassic()
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "nonsense", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, term: "xterm-256color", classic: false));
    }
}
