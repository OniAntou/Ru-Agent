using System.Drawing;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiGalaxySkyTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(160, 50)]
    [InlineData(240, 100)]
    public void Galaxy_is_deterministic_animated_and_contains_core_and_arms(int width, int height)
    {
        var sky = new TuiGalaxySky();
        sky.Render(width, height, 1.25);
        var first = sky.Dots.ToArray();
        var colors = sky.Colors.ToArray();
        Assert.Contains((byte)8, colors);
        Assert.Contains((byte)6, colors);
        Assert.All(colors, color => Assert.InRange(color, (byte)0, (byte)8));
        Assert.Equal(width * height, first.Length);
        sky.Render(width, height, 1.25);
        Assert.Equal(first, sky.Dots.ToArray());
        Assert.Equal(colors, sky.Colors.ToArray());
        sky.Render(width, height, 2.5);
        Assert.False(first.SequenceEqual(sky.Dots.ToArray()));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(int.MaxValue, int.MaxValue, 240, 100)]
    public void Resize_and_extreme_dimensions_are_bounded(int width, int height, int columns, int rows)
    {
        var sky = new TuiGalaxySky();
        sky.Render(80, 24, 100);
        sky.Render(width, height, double.NaN);
        Assert.Equal(columns, sky.Columns); Assert.Equal(rows, sky.Rows);
        Assert.Equal(columns * rows, sky.Dots.Length);
        Assert.Equal(sky.Dots.Length, sky.Colors.Length);
        sky.Render(40, 12, double.PositiveInfinity);
        Assert.Equal(480, sky.Dots.Length);
    }

    [Fact]
    public void Steady_frames_reuse_buffers_without_managed_allocation()
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) sky.Render(160, 50, i * 0.07);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Captured from the approved browser preview at 80x24 cells using its JavaScript renderer.
    [Theory]
    [InlineData(0, "6089B2A11A97B3700A88EA4C1E31F2B6C0F416D00A873A6A940A6E6EACE42A6F", "E4828717C41F3F8B2B7590942575D48B1A88652D41BD2E6E621F45F5F6E19947")]
    [InlineData(3, "3E0027AC040B54C6DCE2258A883D2D6A03F6AC0C1913A1F8878711DB1146DC52", "1DAF0B7F07BC9E5DD6B0CD4C69A684B933CFC81C93E874FF88BF520DCE525C9B")]
    [InlineData(8, "B2ED8112BBD928CE307166BB74BB4DB6D543B05ECA2D70DAD3862E38BE286282", "4324C0B0D27FF0D175C3744A8674EB80088EE3A727A90DD1B13FE8060EA87E7D")]
    [InlineData(13, "88F3A553E33721783FFE6E3E706C6F6F86C3F659B013DAB98F3A66A885B49950", "EB8F98EDBE49AC879B07E76FBB6A97CF8D02682AF7021C3A42BB57DAECA0F161")]
    public void Frames_match_approved_wide_inward_spiral_preview(int seconds, string dots, string colors)
    {
        var sky = new TuiGalaxySky();
        sky.Render(80, 24, seconds);
        Assert.Equal(dots, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sky.Dots)));
        Assert.Equal(colors, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sky.Colors)));
    }

    [Fact]
    public void Detailed_layers_retain_intermediate_glow_between_dark_dust_and_nucleus()
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, 4);
        var colors = sky.Colors.ToArray();
        foreach (var tone in new byte[] { 2, 4, 5, 6, 7, 8 }) Assert.Contains(tone, colors);
    }

    [Fact]
    public void Bright_disc_slants_upward_to_the_right_on_screen()
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, 3);
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < sky.Colors.Length; i++)
            if (sky.Colors[i] is >= 4 and <= 7) points.Add((i % 160, i / 160));
        var meanX = points.Average(p => p.X); var meanY = points.Average(p => p.Y);
        var covariance = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var varianceX = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        var varianceY = points.Sum(p => (p.Y - meanY) * (p.Y - meanY));
        Assert.InRange(covariance / Math.Sqrt(varianceX * varianceY), -1, -0.05);
    }

    [Fact]
    public void Core_keeps_bright_center_and_translucent_edge()
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, 3);
        Assert.Equal((byte)8, sky.Colors[32 * 160 + 80]);
        Assert.Contains((byte)5, sky.Colors.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void Bright_core_does_not_collapse_into_solid_braille_blocks(int seconds)
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, seconds);
        var bright = 0; var solid = 0;
        for (var y = 27; y <= 37; y++)
        for (var x = 70; x <= 90; x++)
        {
            var index = y * 160 + x;
            if (sky.Colors[index] < 7) continue;
            bright++;
            if (sky.Dots[index] == byte.MaxValue) solid++;
        }
        Assert.True(bright > 0);
        Assert.InRange((double)solid / bright, 0, 0.10);
    }

    [Fact]
    public void Bright_core_is_lowered_below_the_wordmark()
    {
        var sky = new TuiGalaxySky();
        sky.Render(160, 50, 3);
        Assert.Equal((byte)8, sky.Colors[32 * 160 + 80]);
        Assert.NotEqual((byte)0, sky.Dots[32 * 160 + 80]);
    }

    [Fact]
    public void Welcome_clock_is_throttled_and_stops_when_hidden_or_covered()
    {
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 80, 24) };
        view.Tick(TimeSpan.Zero);
        view.Tick(TimeSpan.FromMilliseconds(20)); Assert.Equal(0, view.AnimationTime);
        view.Tick(TimeSpan.FromMilliseconds(49)); Assert.Equal(0, view.AnimationTime);
        view.Tick(TimeSpan.FromMilliseconds(50)); Assert.Equal(0.05, view.AnimationTime, 6);
        view.Tick(TimeSpan.FromMilliseconds(70)); Assert.Equal(0.05, view.AnimationTime, 6);
        view.Tick(TimeSpan.FromMilliseconds(100)); Assert.Equal(0.10, view.AnimationTime, 6);
        view.Visible = false;
        view.Tick(TimeSpan.FromSeconds(5)); Assert.Equal(0.10, view.AnimationTime, 6);
        view.Visible = true;
        view.Tick(TimeSpan.FromSeconds(10)); Assert.Equal(0.10, view.AnimationTime, 6);
        view.AnimationPaused = true;
        view.Tick(TimeSpan.FromSeconds(11)); Assert.Equal(0.10, view.AnimationTime, 6);
        view.AnimationPaused = false;
        view.Tick(TimeSpan.FromSeconds(12));
        view.Tick(TimeSpan.FromSeconds(12.05)); Assert.Equal(0.15, view.AnimationTime, 6);
        view.Tick(TimeSpan.FromSeconds(100)); Assert.Equal(0.25, view.AnimationTime, 6);
    }
}
