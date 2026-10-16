using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityPaletteTests
{
    [Theory]
    [InlineData(15, 23, 42, 226)]
    [InlineData(238, 238, 238, 20)]
    public void Sidebar_wave_remains_readable_in_both_themes(byte red, byte green, byte blue, byte background)
    {
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        var ink = Windows.UI.Color.FromArgb(255, red, green, blue);
        double backdrop = Linear(background);
        for (int step = 0; step <= 100; step++)
        {
            var sample = AgentActivityPalette.SampleSidebarAuroraColor(0.5, -0.35 + 1.7 * step / 100.0, ink);
            double luminance = 0.2126 * Linear(sample.R) + 0.7152 * Linear(sample.G) + 0.0722 * Linear(sample.B);
            double contrast = (Math.Max(luminance, backdrop) + 0.05) / (Math.Min(luminance, backdrop) + 0.05);
            Assert.True(contrast >= 4.5, $"Wave contrast was {contrast} at step {step}.");
        }
    }

    [Fact]
    public void OutsideBeam_ReturnsBaseColor()
    {
        var baseColor = Windows.UI.Color.FromArgb(255, 10, 20, 30);
        Assert.Equal(baseColor, AgentActivityPalette.SampleAuroraColor(0.0, 0.9, baseColor));
    }

    [Fact]
    public void AtBeamEdge_ReturnsBaseColor()
    {
        var baseColor = Windows.UI.Color.FromArgb(255, 10, 20, 30);
        Assert.Equal(baseColor, AgentActivityPalette.SampleAuroraColor(0.0, AgentActivityPalette.BeamRadius, baseColor));
    }

    [Fact]
    public void Lerp_EndpointsAreExact()
    {
        var from = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        var to = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        Assert.Equal(from, AgentActivityPalette.LerpColor(from, to, 0.0));
        Assert.Equal(to, AgentActivityPalette.LerpColor(from, to, 1.0));
    }

    [Fact]
    public void Center_IsDeterministicAndBlended()
    {
        var baseColor = Windows.UI.Color.FromArgb(255, 30, 30, 30);
        var first = AgentActivityPalette.SampleAuroraColor(0.5, 0.5, baseColor);
        var second = AgentActivityPalette.SampleAuroraColor(0.5, 0.5, baseColor);
        Assert.Equal(first, second);
        Assert.NotEqual(baseColor, first);
    }
}
