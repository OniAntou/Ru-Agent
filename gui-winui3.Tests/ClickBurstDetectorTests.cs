using System;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>The New Chat black hole easter egg fires only on a real burst of clicks.</summary>
public sealed class ClickBurstDetectorTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Eight_clicks_inside_the_window_fire_once_then_start_over()
    {
        var detector = new ClickBurstDetector(8, At(2.5));
        for (var click = 0; click < 7; click++) Assert.False(detector.Register(At(click * 0.2)));
        Assert.True(detector.Register(At(1.4)));
        Assert.False(detector.Register(At(1.5)));
    }

    [Fact]
    public void Ordinary_clicking_never_fires()
    {
        var detector = new ClickBurstDetector(8, At(2.5));
        for (var click = 0; click < 40; click++) Assert.False(detector.Register(At(click * 0.5)));
    }

    [Fact]
    public async Task The_black_hole_shader_compiles()
    {
        var shaders = await RuApp.Controls.BlackHoleRenderer.CompileAsync();
        Assert.All(new[] { shaders.Vertex, shaders.Scene, shaders.Down, shaders.Blur, shaders.Composite }, Assert.NotEmpty);
    }

    [Fact]
    public void The_easter_egg_is_wired_to_the_sidebar_button_and_lives_above_the_shell()
    {
        var handler = GuiSource.MethodBody(GuiSource.Text("MainWindow.xaml.cs"), "OnNewChatClick");
        Assert.Contains("ReferenceEquals(sender, NavNewChat)", handler, StringComparison.Ordinal);
        Assert.Contains("PlayBlackHoleAsync", handler, StringComparison.Ordinal);
        var overlay = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "BlackHole");
        Assert.Equal("900", GuiSource.AttributeValue(overlay, "Canvas.ZIndex"));
    }
}
