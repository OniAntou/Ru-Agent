using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Which chat renderer drives <c>agent chat</c>.</summary>
public enum TuiEngine
{
    Sequential,
    TerminalGui,
}

/// <summary>
/// Engine resolution for the chat TUI. Terminal.Gui is the default; a redirected, JSON,
/// NO_COLOR, dumb or small terminal stays on the sequential fallback, and <c>--classic</c>
/// forces it explicitly.
/// </summary>
public static class TuiEngineSelection
{
    public static TuiEngine Resolve(
        bool outputRedirected,
        bool json,
        int columns,
        int rows,
        string? noColor,
        string? term,
        bool classic)
    {
        if (outputRedirected || json || classic || noColor is not null)
        {
            return TuiEngine.Sequential;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return TuiEngine.Sequential;
        }

        if (columns < TuiViewport.MinAltBufferWidth || rows < TuiViewport.MinAltBufferHeight)
        {
            return TuiEngine.Sequential;
        }

        return TuiEngine.TerminalGui;
    }
}
