using RuAgent.Cli.Tui;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Cached cell glyphs and starlight colors for the native welcome wordmark.</summary>
internal static class TgWelcomeWordmark
{
    internal const int SweepPeriodSeconds = 10;
    private const int LightLevels = 16;
    private static readonly string[] Rows = Enumerable.Range(0, TuiHeader.WordmarkRows)
        .Select(row => TuiHeader.WordmarkRuRows[row] + " " + TuiHeader.WordmarkAgentRows[row]).ToArray();
    internal static int Width { get; } = Rows.Max(row => row.Length);
    private static readonly string[] Glyphs = BuildGlyphs();
    private static readonly string[] ShadowGlyphs = BuildShadow();
    private static readonly Attr[] Lights = BuildLights();
    internal static readonly Attr ShadowAttribute = TgPalette.Attr((13, 34, 38));

    internal static string Glyph(int row, int column) => Glyphs[row * Width + column];
    internal static string ShadowGlyph(int column) => ShadowGlyphs[column];

    // Two seconds of quiet welcome precede the first sweep. The remaining
    // interval is quiet too; the light never pulses the whole logo at once.
    internal static double SweepPosition(double time)
    {
        if (!double.IsFinite(time) || time < 0) return double.NaN;
        var phase = (time % SweepPeriodSeconds + 8) % SweepPeriodSeconds;
        return phase < 2.5 ? -5 + phase / 2.5 * (Width + 10) : double.NaN;
    }

    internal static Attr AttributeAt(int row, int column, double sweepPosition)
    {
        var light = double.IsNaN(sweepPosition) ? 0
            : Math.Max(0, 1 - Math.Abs(column + row * 0.7 - sweepPosition) / 3.5);
        var level = (int)Math.Round(light * (LightLevels - 1), MidpointRounding.AwayFromZero);
        return Lights[(row * Width + column) * LightLevels + level];
    }

    private static string[] BuildGlyphs()
    {
        var glyphs = new string[TuiHeader.WordmarkRows * Width];
        for (var row = 0; row < TuiHeader.WordmarkRows; row++)
        for (var column = 0; column < Width; column++)
            glyphs[row * Width + column] = column < Rows[row].Length ? Rows[row][column].ToString() : " ";
        return glyphs;
    }

    private static string[] BuildShadow()
    {
        var shadow = new string[Width];
        for (var column = 0; column < Width; column++)
            shadow[column] = Glyph(TuiHeader.WordmarkRows - 1, column) switch
            {
                "█" or "▄" => "▀", var glyph => glyph,
            };
        return shadow;
    }

    private static Attr[] BuildLights()
    {
        var lights = new Attr[TuiHeader.WordmarkRows * Width * LightLevels];
        var ruWidth = TuiHeader.WordmarkRuRows[0].Length;
        for (var row = 0; row < TuiHeader.WordmarkRows; row++)
        for (var column = 0; column < Width; column++)
        {
            var color = column < ruWidth
                ? Mix((112, 225, 187), (100, 187, 226), (double)column / ruWidth)
                : Mix((100, 180, 135), (72, 156, 112), (double)(column - ruWidth - 1) / (Width - ruWidth - 1));
            if (row == 0) color = Mix(color, (247, 255, 253), 0.16);
            for (var level = 0; level < LightLevels; level++)
                lights[(row * Width + column) * LightLevels + level] =
                    TgPalette.Attr(Mix(color, (255, 253, 233), level / (double)(LightLevels - 1) * 0.86));
        }
        return lights;
    }

    private static (int R, int G, int B) Mix((int R, int G, int B) a, (int R, int G, int B) b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
        int Channel(int from, int to) => (int)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);
    }
}
