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

/// <summary>
/// Pairs the alt-buffer enter/exit around one interactive session. A disabled scope writes
/// nothing at all, so redirected, <c>--json</c> and <c>--classic</c> runs keep their existing
/// bytes; an enabled one writes <see cref="TuiAltBuffer.Enter"/> once through the shared
/// surface and <see cref="TuiAltBuffer.Exit"/> exactly once on disposal, whatever exit path
/// the session takes (return, cancellation or exception).
/// </summary>
public sealed class TuiAltBufferScope : IDisposable
{
    private readonly TuiSurface _surface;
    private bool _active;

    public TuiAltBufferScope(TuiSurface surface, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        if (!enabled)
        {
            return;
        }

        _surface.EnterAltBuffer();
        _active = true;
    }

    /// <summary>True when this scope owns an entered alt buffer that Dispose will exit.</summary>
    public bool Active => _active;

    public void Dispose()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _surface.ExitAltBuffer();
    }
}
