using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Which full-screen renderer drives <c>agent chat</c>.</summary>
public enum TuiEngine
{
    Classic,
    TerminalGui,
}

/// <summary>
/// Engine resolution for the chat TUI. The terminal gates mirror the classic AltBuffer
/// gate (<c>TuiAltBuffer.ShouldUseAltBuffer</c>): a redirected, JSON, NO_COLOR, dumb or
/// small terminal always stays classic. Parity reached (2026-10-03), so Terminal.Gui is
/// the default and <c>RU_TUI_ENGINE=classic</c> is the one-release escape hatch back to
/// the hand-rolled full-screen renderer.
/// </summary>
public static class TuiEngineSelection
{
    public const string EngineVariable = "RU_TUI_ENGINE";

    public static TuiEngine Resolve(
        string? envValue,
        bool outputRedirected,
        bool json,
        int columns,
        int rows,
        string? noColor,
        string? term,
        bool classic)
    {
        if (outputRedirected || json || classic)
        {
            return TuiEngine.Classic;
        }

        if (noColor is not null)
        {
            return TuiEngine.Classic;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return TuiEngine.Classic;
        }

        if (columns < TuiViewport.MinAltBufferWidth || rows < TuiViewport.MinAltBufferHeight)
        {
            return TuiEngine.Classic;
        }

        return string.Equals(envValue?.Trim(), "classic", StringComparison.OrdinalIgnoreCase)
            ? TuiEngine.Classic
            : TuiEngine.TerminalGui;
    }
}
