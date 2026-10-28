using RuAgent.Cli.TerminalGui;
using Terminal.Gui.Drawing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSquareWordmarkRasterTests
{
    [Fact]
    public void Tiles_have_clear_gutters_transparent_surroundings_and_the_existing_colors()
    {
        var pixels = new TgSquareWordmarkRaster().Render(0);
        Assert.Equal(TgSquareWordmarkRaster.Width, pixels.GetLength(0));
        Assert.Equal(TgSquareWordmarkRaster.Height, pixels.GetLength(1));
        Assert.Equal(0, pixels[0, 0].A);
        Assert.Equal(0, pixels[pixels.GetLength(0) - 1, pixels.GetLength(1) - 1].A);
        Assert.Equal(new Color(0, 0, 0, 255), pixels[12, 12]);
        Assert.Equal(TgWelcomeWordmark.AttributeAt(0, 0, double.NaN).Foreground, pixels[17, 17]);
        Assert.Equal(TgWelcomeWordmark.AttributeAt(0, 10, double.NaN).Foreground, pixels[117, 17]);
        // Inner RU joints are subtle; AGENT keeps its original black grid.
        var seam = pixels[22, 17]; var face = pixels[27, 17];
        Assert.InRange(seam.R / (double)face.R, .75, .85);
        Assert.InRange(seam.G / (double)face.G, .75, .85);
        Assert.InRange(seam.B / (double)face.B, .75, .85);
        Assert.Equal(255, seam.A);
        Assert.Equal(new Color(0, 0, 0, 255), pixels[122, 17]);
        // Empty inter-letter space stays transparent rather than a black image rectangle.
        Assert.Equal(0, pixels[12 + 9 * 10 + 5, 12].A);
    }

    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(10)] [InlineData(16)]
    [InlineData(21)] [InlineData(25)] [InlineData(31)]
    public void Every_letter_has_a_projecting_lower_rim(int column)
    {
        var pixels = new TgSquareWordmarkRaster().Render(0);
        var source = TgWelcomeWordmark.AttributeAt(2, column, double.NaN).Foreground;
        var expected = new Color((int)Math.Round(source.R * .30), (int)Math.Round(source.G * .30),
            (int)Math.Round(source.B * .30), 255);
        Assert.Equal(expected, pixels[12 + column * 10 + 12, 12 + 60 + 3]);
    }

    [Fact]
    public void Shadow_follows_the_whole_silhouette_without_filling_the_tile_gutters()
    {
        var pixels = new TgSquareWordmarkRaster().Render(0);
        Assert.True(pixels[12 + 33 * 10 + 3, 12 + 4].A > 0);
        Assert.Equal(new Color(0, 0, 0, 255), pixels[12 + 32 * 10, 12]);
    }

    [Fact]
    public void Frames_use_two_buffers_and_do_not_redraw_or_allocate_during_quiet_time()
    {
        var raster = new TgSquareWordmarkRaster();
        var quiet = raster.Render(0);
        Assert.Same(quiet, raster.Render(1));
        var lit = raster.Render(3);
        Assert.NotSame(quiet, lit);
        Assert.NotEqual(quiet[137, 17], lit[137, 17]);
        var other = raster.Render(3.2);
        Assert.Same(quiet, other);
        Assert.Same(other, raster.Render(3.21));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) _ = raster.Render(i * .1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void Invalid_time_keeps_the_quiet_frame(double time)
    {
        var raster = new TgSquareWordmarkRaster();
        Assert.Same(raster.Render(0), raster.Render(time));
    }
}
