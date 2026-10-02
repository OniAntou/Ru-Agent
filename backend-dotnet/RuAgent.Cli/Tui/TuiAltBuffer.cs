namespace RuAgent.Cli.Tui;

public static class TuiAltBuffer
{
    public const string Enter = "\u001b[?1049h";
    public const string Exit = "\u001b[?1049l";

    public static bool ShouldUseAltBuffer(
        bool outputRedirected, string? term, string? noColor,
        int columns, int rows, bool classic, bool json)
    {
        if (outputRedirected || classic || json)
        {
            return false;
        }

        if (noColor is not null)
        {
            return false;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return columns >= TuiViewport.MinAltBufferWidth && rows >= TuiViewport.MinAltBufferHeight;
    }
}
