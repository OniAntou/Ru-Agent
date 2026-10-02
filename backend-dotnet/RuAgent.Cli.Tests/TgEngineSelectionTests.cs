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
            columns: 120, rows: 40, noColor: null, classic: false));
    }

    [Fact]
    public void Resolve_TerminalGuiOptIn_LiveTerminal_ReturnsTerminalGui()
    {
        Assert.Equal(TuiEngine.TerminalGui, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, classic: false));
    }

    [Theory]
    [InlineData(true, false, null, 120, 40, false)]  // redirected
    [InlineData(false, true, null, 120, 40, false)]  // json
    [InlineData(false, false, "1", 120, 40, false)]  // NO_COLOR
    [InlineData(false, false, null, 79, 40, false)]  // too narrow
    [InlineData(false, false, null, 120, 23, false)] // too short
    [InlineData(false, false, null, 120, 40, true)]  // classic flag
    public void Resolve_TerminalGuiRequested_ButGateFails_ReturnsClassic(
        bool outputRedirected, bool json, string? noColor, int columns, int rows, bool classic)
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected, json,
            columns, rows, noColor, classic));
    }

    [Fact]
    public void Resolve_UnknownValue_ReturnsClassic()
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "nonsense", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, classic: false));
    }
}
