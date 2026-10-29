using Terminal.Gui.Drawing;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Bounded, transparent tile logo; two reused buffers keep ImageView's image cache fresh.</summary>
internal sealed class TgSquareWordmarkRaster
{
    internal const int Tile = 10;
    internal const int Padding = 12;
    internal static int Width => TgWelcomeWordmark.Width * Tile + Padding * 2;
    internal const int Height = 6 * Tile + Padding * 2;
    private static readonly bool[] Mask = BuildMask();
    private static readonly Color Black = new(0, 0, 0, 255);
    private static readonly (int X, int Y, Color Color)[] Shadows =
    [
        (11, 8, new(5, 12, 17)), (8, 6, new(8, 19, 25)),
        (5, 4, new(12, 28, 34)), (3, 2, new(19, 39, 44)),
    ];
    private readonly Color[][,] _buffers = [new Color[Width, Height], new Color[Width, Height]];
    private int _buffer;
    private int _frameKey = -1;

    internal static int FrameKey(double time)
    {
        if (!double.IsFinite(time) || time < 0) return 0;
        var phase = (time % TgWelcomeWordmark.SweepPeriodSeconds + 8) % TgWelcomeWordmark.SweepPeriodSeconds;
        return phase < 2.5 ? 1 + Math.Min(24, (int)Math.Floor(phase * 10 + 0.000001)) : 0;
    }

    internal Color[,] Render(double time)
    {
        var key = FrameKey(time);
        if (key == _frameKey) return _buffers[_buffer];
        _frameKey = key;
        _buffer ^= 1;
        var pixels = _buffers[_buffer];
        Array.Clear(pixels);
        var sweep = TgWelcomeWordmark.SweepPosition(time);
        foreach (var shadow in Shadows)
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
            if (Filled(x, y)) Rect(pixels, x * Tile + shadow.X, y * Tile + shadow.Y, Tile, Tile, shadow.Color);
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
        {
            if (!Filled(x, y)) continue;
            var color = TgWelcomeWordmark.AttributeAt(y / 2, x, sweep).Foreground;
            if (!Filled(x + 1, y)) Rect(pixels, (x + 1) * Tile, y * Tile + 1, 2, 9, Shade(color, .22));
            if (!Filled(x, y + 1) && y < 5) Rect(pixels, x * Tile + 2, (y + 1) * Tile, 9, 2, Shade(color, .25));
            if (y == 5) Rect(pixels, x * Tile + 8, 6 * Tile, 9, 6, Shade(color, .30));
        }
        // Preserve the black outer silhouette beneath the softened inner seams.
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
            if (Filled(x, y)) Rect(pixels, x * Tile, y * Tile, Tile, Tile, Black);
        // All letters' inner seams represent a subpixel gap by partial coverage. This
        // softens the grid without changing the outline or enlarging the raster.
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
        {
            if (!Filled(x, y)) continue;
            var color = TgWelcomeWordmark.AttributeAt(y / 2, x, sweep).Foreground;
            var left = Filled(x - 1, y);
            var above = Filled(x, y - 1);
            var seam = Shade(color, .80);
            if (left) Rect(pixels, x * Tile, y * Tile + 1, 1, Tile - 1, seam);
            if (above) Rect(pixels, x * Tile + 1, y * Tile, Tile - 1, 1, seam);
            if (left && above && Filled(x - 1, y - 1))
                Rect(pixels, x * Tile, y * Tile, 1, 1, Shade(color, .64));
        }
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
            if (Filled(x, y)) Rect(pixels, x * Tile + 1, y * Tile + 1, 9, 9,
                TgWelcomeWordmark.AttributeAt(y / 2, x, sweep).Foreground);
        return pixels;
    }

    private static bool Filled(int x, int y) => x >= 0 && x < TgWelcomeWordmark.Width && y >= 0 && y < 6
        && Mask[y * TgWelcomeWordmark.Width + x];

    private static bool[] BuildMask()
    {
        var mask = new bool[TgWelcomeWordmark.Width * 6];
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < TgWelcomeWordmark.Width; x++)
        {
            var glyph = TgWelcomeWordmark.Glyph(y / 2, x);
            mask[y * TgWelcomeWordmark.Width + x] = glyph == "█" || glyph == "▀" && y % 2 == 0 || glyph == "▄" && y % 2 == 1;
        }
        return mask;
    }

    private static Color Shade(Color color, double amount) => new(
        (int)Math.Round(color.R * amount), (int)Math.Round(color.G * amount), (int)Math.Round(color.B * amount), 255);

    private static void Rect(Color[,] pixels, int x, int y, int width, int height, Color color)
    {
        var right = Math.Min(Width, Padding + x + width);
        var bottom = Math.Min(Height, Padding + y + height);
        for (var yy = Math.Max(0, Padding + y); yy < bottom; yy++)
        for (var xx = Math.Max(0, Padding + x); xx < right; xx++) pixels[xx, yy] = color;
    }
}
