using RuAgent.Cli.TerminalGui;
using Terminal.Gui.Drawing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWordmarkPaletteTests
{
    [Fact]
    public void Beam_on_RU_does_not_recolor_unchanged_AGENT_tiles_or_seams()
    {
        var raster = new TgSquareWordmarkRaster();
        var encoder = new SixelEncoder
        {
            Quantizer = new ColorQuantizer { MaxColors = 32, PaletteBuildingAlgorithm = TgWordmarkPalette.Instance },
        };
        var quiet = raster.Render(0);
        _ = encoder.EncodeSixel(quiet);
        var palette = encoder.Quantizer.Palette.ToArray();
        var colors = new Color[quiet.GetLength(0), quiet.GetLength(1)];
        for (var y = 0; y < quiet.GetLength(1); y++)
        for (var x = 112; x < quiet.GetLength(0); x++)
            if (quiet[x, y].A > 0) colors[x, y] = palette[encoder.Quantizer.GetNearestColor(quiet[x, y])];

        var lit = raster.Render(2.5); // The beam is still over RU, not AGENT.
        _ = encoder.EncodeSixel(lit);
        palette = encoder.Quantizer.Palette.ToArray();
        var changed = 0;
        for (var y = 0; y < lit.GetLength(1); y++)
        for (var x = 112; x < lit.GetLength(0); x++)
        {
            if (lit[x, y].A == 0) continue;
            Assert.Equal(quiet[x, y], lit[x, y]);
            if (colors[x, y] != palette[encoder.Quantizer.GetNearestColor(lit[x, y])]) changed++;
        }
        Assert.True(changed == 0, $"The untouched AGENT region recolored {changed} pixels.");
    }

    [Theory]
    [InlineData(4)] [InlineData(16)] [InlineData(32)]
    public void Palette_is_reused_across_the_sweep_and_stays_within_the_terminal_limit(int limit)
    {
        var builder = TgWordmarkPalette.Instance;
        var palette = builder.BuildPalette(limit);
        Assert.InRange(palette.Count, 1, limit);
        Assert.Contains(new Color(0, 0, 0, 255), palette);
        Assert.Same(palette, builder.BuildPalette(limit));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var lookup = 0; lookup < 300; lookup++) _ = builder.BuildPalette(limit);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var quantizer = new ColorQuantizer { MaxColors = limit, PaletteBuildingAlgorithm = builder };
        var raster = new TgSquareWordmarkRaster();
        for (var frame = 0; frame < 27; frame++)
        {
            quantizer.BuildPalette(raster.Render(frame == 26 ? 10 : 1.95 + frame * .1));
            Assert.Equal(palette, quantizer.Palette);
        }
    }

    [Fact]
    public void Fixed_palette_keeps_green_faces_soft_seams_and_a_brighter_warm_beam()
    {
        var palette = TgWordmarkPalette.Instance.BuildPalette(32).ToArray();
        var quantizer = new ColorQuantizer { MaxColors = 32, PaletteBuildingAlgorithm = TgWordmarkPalette.Instance };
        var raster = new TgSquareWordmarkRaster();
        var quiet = raster.Render(0);
        quantizer.BuildPalette(quiet);
        var face = palette[quantizer.GetNearestColor(quiet[127, 17])];
        var seam = palette[quantizer.GetNearestColor(quiet[122, 17])];
        Assert.True(face.G > face.R && face.G > face.B);
        Assert.True(Light(face) - Light(seam) > 10);
        var peak = TgWelcomeWordmark.AttributeAt(0, 11, 11).Foreground;
        Assert.True(Light(palette[quantizer.GetNearestColor(peak)]) - Light(face) > 40);

        static double Light(Color color) => color.R * .2126 + color.G * .7152 + color.B * .0722;
    }
}
