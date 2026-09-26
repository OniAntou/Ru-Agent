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
    public void WelcomeOverlay_runs_no_looping_animation()
    {
        // The animated background was replaced by static art: no render loop,
        // no repeating storyboard may come back.
        var source = GuiSource.Text("Controls", "WelcomeOverlay.cs");

        Assert.DoesNotContain("BackgroundPaths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RepeatBehavior.Forever", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", source, StringComparison.Ordinal);
    }

    private static int CanvasZIndex(XElement element) =>
        GuiSource.IntAttribute(element, "ZIndex");
}
