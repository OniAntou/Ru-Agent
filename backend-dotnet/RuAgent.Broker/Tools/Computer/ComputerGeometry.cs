namespace RuAgent.Broker.Tools.Computer;

/// <summary>A rectangle in physical virtual-desktop pixels; right and bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public PixelRect Intersect(PixelRect other) => new(
        Math.Max(Left, other.Left),
        Math.Max(Top, other.Top),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));

    /// <summary>The parts of this rectangle outside <paramref name="cut"/>, as up to four disjoint pieces.</summary>
    public IEnumerable<PixelRect> Subtract(PixelRect cut)
    {
        var overlap = Intersect(cut);
        if (overlap.IsEmpty)
        {
            yield return this;
            yield break;
        }

        if (overlap.Top > Top) yield return new PixelRect(Left, Top, Right, overlap.Top);
        if (overlap.Bottom < Bottom) yield return new PixelRect(Left, overlap.Bottom, Right, Bottom);
        if (overlap.Left > Left) yield return new PixelRect(Left, overlap.Top, overlap.Left, overlap.Bottom);
        if (overlap.Right < Right) yield return new PixelRect(overlap.Right, overlap.Top, Right, overlap.Bottom);
    }
}

public sealed record DesktopMonitor(int Index, PixelRect Bounds, bool Primary);

public sealed record DesktopWindow(
    nint Handle,
    PixelRect Bounds,
    int ProcessId,
    string AppName,
    string? ExecutablePath,
    string Title);

/// <summary>Pure geometry for computer use: masks and screenshot coordinates (ADR 0031).</summary>
public static class ComputerGeometry
{
    /// <summary>
    /// The visible parts of allowed windows on one monitor. <paramref name="zOrder"/>
    /// lists visible top-level windows from the top down; every window, allowed or
    /// not, hides what lies beneath it.
    /// </summary>
    public static IReadOnlyList<PixelRect> VisibleAllowedRegions(
        IEnumerable<DesktopWindow> zOrder,
        Func<DesktopWindow, bool> allowed,
        PixelRect monitor)
    {
        var uncovered = new List<PixelRect> { monitor };
        var visible = new List<PixelRect>();
        foreach (var window in zOrder)
        {
            var bounds = window.Bounds.Intersect(monitor);
            if (bounds.IsEmpty || uncovered.Count == 0) continue;
            var next = new List<PixelRect>(uncovered.Count + 4);
            foreach (var region in uncovered)
            {
                var shown = region.Intersect(bounds);
                if (!shown.IsEmpty && allowed(window)) visible.Add(shown);
                next.AddRange(region.Subtract(bounds).Where(piece => !piece.IsEmpty));
            }

            uncovered = next;
        }

        return visible;
    }

    /// <summary>Blacks out every pixel of a top-down BGRA bitmap of <paramref name="monitor"/> outside the regions.</summary>
    public static void MaskOutside(byte[] bgra, PixelRect monitor, IReadOnlyList<PixelRect> keep)
    {
        var width = monitor.Width;
        var height = monitor.Height;
        if (bgra.Length < width * height * 4) throw new ArgumentException("Bitmap is smaller than the monitor.", nameof(bgra));
        var keepMask = new bool[width * height];
        foreach (var region in keep)
        {
            var local = region.Intersect(monitor);
            for (var y = local.Top; y < local.Bottom; y++)
            {
                Array.Fill(keepMask, true, (y - monitor.Top) * width + (local.Left - monitor.Left), local.Width);
            }
        }

        for (var index = 0; index < keepMask.Length; index++)
        {
            if (keepMask[index]) continue;
            var offset = index * 4;
            bgra[offset] = 0;
            bgra[offset + 1] = 0;
            bgra[offset + 2] = 0;
        }
    }

    /// <summary>The screenshot size for a monitor: the long edge at most <paramref name="maxEdge"/>, aspect kept.</summary>
    public static (int Width, int Height) ScreenshotSize(PixelRect monitor, int maxEdge)
    {
        var longEdge = Math.Max(monitor.Width, monitor.Height);
        if (longEdge <= maxEdge) return (monitor.Width, monitor.Height);
        var scale = (double)maxEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(monitor.Width * scale)), Math.Max(1, (int)Math.Round(monitor.Height * scale)));
    }

    /// <summary>Maps a point in a monitor's screenshot to the physical pixel it shows.</summary>
    public static (int X, int Y) ToPhysical(PixelRect monitor, int shotWidth, int shotHeight, int x, int y)
    {
        if (x < 0 || y < 0 || x >= shotWidth || y >= shotHeight)
            throw new ComputerUseException("computer_point_outside_screenshot",
                $"x and y must lie inside the {shotWidth}x{shotHeight} screenshot.");
        var px = monitor.Left + (int)Math.Min(monitor.Width - 1, Math.Floor((x + 0.5) * monitor.Width / shotWidth));
        var py = monitor.Top + (int)Math.Min(monitor.Height - 1, Math.Floor((y + 0.5) * monitor.Height / shotHeight));
        return (px, py);
    }
}

public sealed class ComputerUseException(string code, string detail) : Exception(code)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}
