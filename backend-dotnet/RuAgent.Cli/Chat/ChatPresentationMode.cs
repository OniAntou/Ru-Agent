using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

/// <summary>Which output adapter presents <c>agent chat</c>.</summary>
public enum ChatPresentationMode
{
    Plain,
    Native,
}

/// <summary>
/// Output capability selection; conversation handling always uses ChatSessionEngine. Terminal.Gui is the default; redirected input/output, JSON,
/// NO_COLOR, dumb or small terminal uses append-only line output, and <c>--classic</c>
/// forces it explicitly.
/// </summary>
public static class ChatPresentationSelection
{
    public static ChatPresentationMode Resolve(
        bool outputRedirected,
        bool json,
        int columns,
        int rows,
        string? noColor,
        string? term,
        bool classic,
        bool inputRedirected = false)
    {
        if (inputRedirected || outputRedirected || json || classic || noColor is not null)
        {
            return ChatPresentationMode.Plain;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return ChatPresentationMode.Plain;
        }

        if (columns < TuiViewport.MinAltBufferWidth || rows < TuiViewport.MinAltBufferHeight)
        {
            return ChatPresentationMode.Plain;
        }

        return ChatPresentationMode.Native;
    }
}
