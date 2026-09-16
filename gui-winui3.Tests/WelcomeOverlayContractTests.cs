using System.Xml.Linq;
using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Welcome overlay and background animation contract. Markup is read as XML so the
/// assertions describe the element tree, and the animation geometry is asserted
/// against the control's public constants instead of the text of its source.
/// </summary>
public sealed class WelcomeOverlayContractTests
{
    [Fact]
    public void MainWindow_hosts_the_welcome_overlay_above_the_shell()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var named = GuiSource.NamedElements(document);

        Assert.True(named.ContainsKey("WelcomeOverlay"), "WelcomeOverlay must be a named element.");

        var overlay = named["WelcomeOverlay"];
        Assert.Equal("WelcomeOverlay", overlay.Name.LocalName);

        // It is the Ru control, not a same-named template in the default namespace.
        Assert.Equal("using:RuApp.Controls", overlay.Name.NamespaceName);

        // It covers the shell rather than sharing a row with it.
        Assert.Equal("2", GuiSource.AttributeValue(overlay, "RowSpan"));

        // And it is drawn last so the shell never paints over it.
        Assert.True(GuiSource.IntAttribute(overlay, "ZIndex") >= 1000);
    }

    [Fact]
    public void WelcomeOverlay_keeps_its_ready_and_dismissed_state_on_the_control()
    {
        // State that the shell reads back must be part of the control's surface;
        // the shell drives it through these members rather than its own flags.
        var type = typeof(WelcomeOverlay);
        Assert.NotNull(type.GetProperty(nameof(WelcomeOverlay.IsReady)));
        Assert.NotNull(type.GetProperty(nameof(WelcomeOverlay.IsDismissed)));
        Assert.NotNull(type.GetEvent("Dismissed"));
        Assert.NotNull(type.GetMethod(nameof(WelcomeOverlay.SetStatus)));
        Assert.NotNull(type.GetMethod(nameof(WelcomeOverlay.NotifyReady)));
        Assert.NotNull(type.GetMethod(nameof(WelcomeOverlay.DismissAsync)));
    }

    [Fact]
    public void Background_paths_control_exposes_its_canvas_geometry()
    {
        // The control scales its curves against these numbers, so they are part of
        // the render contract rather than a private implementation detail.
        Assert.Equal(36, BackgroundPathsControl.PathCount);
        Assert.Equal(696.0, BackgroundPathsControl.CanvasWidth);
        Assert.Equal(316.0, BackgroundPathsControl.CanvasHeight);
    }

    [Fact]
    public void Background_paths_control_is_disposable_for_its_animation_timer()
    {
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(typeof(BackgroundPathsControl)),
            "The control owns a timer and must be disposable.");
    }

    private static int CanvasZIndex(XElement element) =>
        GuiSource.IntAttribute(element, "ZIndex");
}
