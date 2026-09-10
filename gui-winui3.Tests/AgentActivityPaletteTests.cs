using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityPaletteTests
{
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
