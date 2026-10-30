using Terminal.Gui.Drawing;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One bounded Sixel palette per terminal limit, shared by all sweep frames.</summary>
internal sealed class TgWordmarkPalette : IPaletteBuilder, IStaticPaletteBuilder
{
    internal static readonly TgWordmarkPalette Instance = new();
    private readonly object _gate = new();
    private readonly List<Color>?[] _palettes = new List<Color>?[33];
    private readonly Lazy<List<Color>> _referenceColors = new(BuildReferenceColors);

    private TgWordmarkPalette() { }

    public List<Color> BuildPalette(List<Color> colors, int maxColors) => BuildPalette(maxColors);

    public List<Color> BuildPalette(int maxColors)
    {
        var limit = Math.Clamp(maxColors, 1, 32);
        lock (_gate)
        {
            return _palettes[limit] ??= BuildStablePalette(limit);
        }
    }

    private List<Color> BuildStablePalette(int limit)
    {
        // A narrow beam covers few pixels. Popularity alone can discard its
        // brightest colors even though they are essential to the effect.
        var highlights = limit >= 24 ? 3 : limit >= 8 ? 2 : limit >= 2 ? 1 : 0;
        var palette = new PopularityPaletteWithThreshold(new EuclideanColorDistance(), 8)
            .BuildPalette(_referenceColors.Value, limit - highlights);
        var black = new Color(0, 0, 0, 255);
        if (!palette.Contains(black))
        {
            var darkest = 0;
            for (var index = 1; index < palette.Count; index++)
                if (Light(palette[index]) < Light(palette[darkest])) darkest = index;
            palette[darkest] = black;
        }
        if (highlights >= 3) palette.Add(new Color(200, 223, 184));
        if (highlights >= 2) palette.Add(new Color(219, 237, 207));
        if (highlights >= 1) palette.Add(new Color(242, 247, 225));
        return palette;

        static int Light(Color color) => color.R + color.G + color.B;
    }

    private static List<Color> BuildReferenceColors()
    {
        var raster = new TgSquareWordmarkRaster();
        var colors = new List<Color>(26 * ((TgSquareWordmarkRaster.Width + 1) / 2)
            * ((TgSquareWordmarkRaster.Height + 1) / 2));
        Sample(raster.Render(0));
        // Cover every 100 ms sweep frame once. Sampling on even pixels retains
        // the one-pixel seams and their intersections as well as the tile faces.
        for (var frame = 0; frame < 25; frame++) Sample(raster.Render(2 + (frame + .5) / 10));
        return colors;

        void Sample(Color[,] pixels)
        {
            for (var y = 0; y < pixels.GetLength(1); y += 2)
            for (var x = 0; x < pixels.GetLength(0); x += 2)
                if (pixels[x, y].A > 0) colors.Add(pixels[x, y]);
        }
    }
}
