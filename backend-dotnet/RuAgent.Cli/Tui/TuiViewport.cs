namespace RuAgent.Cli.Tui;

/// <summary>
/// Viewport measurement. On a live console the values always describe the terminal as it
/// actually is: the 80x30 fallback is only used when there is no console at all. Claiming a
/// size the terminal does not have is what shreds the layout during a resize — an over-long
/// row wraps, and every absolute cursor move after it lands on the wrong line.
/// </summary>
public static class TuiViewport
{
    public const int FallbackWidth = 80;
    public const int FallbackHeight = 30;

    /// <summary>Below this, frames stop shrinking the two-cell margin and use the full width.</summary>
    public const int MinUsableWidth = 20;

    public static bool IsLiveConsole => !Console.IsOutputRedirected;

    /// <summary>
    /// Raw terminal size (columns, rows) as reported right now, or false when output is
    /// redirected or the console is gone. Values are the terminal's own, transient sizes
    /// included — callers that repaint must debounce through <see cref="TuiResizeWatcher"/>.
    /// </summary>
    public static bool TryReadLiveSize(out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        try
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }

            var liveColumns = Console.WindowWidth;
            var liveRows = Console.WindowHeight;
            if (liveColumns < 1 || liveRows < 1)
            {
                return false;
            }

            columns = liveColumns;
            rows = liveRows;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Columns a frame may use inside a terminal that has <paramref name="columns"/> columns.
    /// Leaves a one-cell margin when there is room, and never exceeds the terminal width.
    /// </summary>
    public static int UsableWidth(int columns, int minWidth = MinUsableWidth)
    {
        if (columns < 1)
        {
            return minWidth;
        }

        if (columns <= minWidth)
        {
            return columns;
        }

        return Math.Min(columns, Math.Max(minWidth, columns - 2));
    }

    /// <summary>Usable columns, or <paramref name="fallback"/> when there is no live console.</summary>
    public static int Width(int fallback = FallbackWidth) =>
        TryReadLiveSize(out var columns, out _) ? UsableWidth(columns) : fallback;

    /// <summary>Live rows, or <paramref name="fallback"/> when there is no live console.</summary>
    public static int Height(int fallback = FallbackHeight) =>
        TryReadLiveSize(out _, out var rows) ? rows : fallback;
}
