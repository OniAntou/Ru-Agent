using RuApp.Services;
using Windows.Foundation;
using Xunit;

namespace RuApp.Tests;

public sealed class TitleBarLayoutTests
{
    [Fact]
    public void The_inset_scales_from_physical_pixels_to_layout_units()
    {
        Assert.Equal(92, TitleBarLayout.InsetInLayoutUnits(138, 1.5), precision: 3);
    }

    [Fact]
    public void A_nonpositive_scale_falls_back_to_raw_pixels()
    {
        Assert.Equal(138, TitleBarLayout.InsetInLayoutUnits(138, 0), precision: 3);
        Assert.Equal(0, TitleBarLayout.InsetInLayoutUnits(-5, 1), precision: 3);
    }

    [Fact]
    public void Bounds_convert_to_physical_pixels()
    {
        var rect = TitleBarLayout.ToPhysicalRect(new Rect(10, 20, 30, 40), 1.5);

        Assert.Equal(15, rect.X);
        Assert.Equal(30, rect.Y);
        Assert.Equal(45, rect.Width);
        Assert.Equal(60, rect.Height);
    }

    [Fact]
    public void Negative_or_tiny_bounds_clamp_at_zero()
    {
        var rect = TitleBarLayout.ToPhysicalRect(new Rect(-4, -6, 0, 0), 1);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }
}
