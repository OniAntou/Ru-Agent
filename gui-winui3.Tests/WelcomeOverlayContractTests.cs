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
    public void WelcomeOverlay_gates_decorative_motion_and_cleans_up_on_disposal()
    {
        // The owner chose Braille waves; both motion owners share OS preferences.
        var source = GuiSource.ViewText("WelcomeOverlay");

        Assert.DoesNotContain("BackgroundPaths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", source, StringComparison.Ordinal);
        var pulse = GuiSource.MethodBody(source, "StartLoadingSweep");
        Assert.Contains("UiTiming.AnimationsEnabled", pulse, StringComparison.Ordinal);
        Assert.Contains("_accentSlide", pulse, StringComparison.Ordinal);
        Assert.DoesNotContain("_ruWordmark", pulse, StringComparison.Ordinal);
        Assert.Contains("StopBrandMotion", GuiSource.MethodBody(source, "Dispose"), StringComparison.Ordinal);
        Assert.Contains("_watchTimer?.Stop()", GuiSource.MethodBody(source, "Dispose"), StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(source, "OnBrailleTick"), StringComparison.Ordinal);
        Assert.Contains("StopBrailleMotion", GuiSource.MethodBody(source, "StopBrandMotion"), StringComparison.Ordinal);
        Assert.Contains("DisposeBraille", GuiSource.MethodBody(source, "Dispose"), StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_content_is_centered_instead_of_stretching_to_the_scroll_viewport_height()
    {
        // Native Measure/Arrange reproduced the top-aligned children when their
        // StackPanel stretches, despite ScrollViewer.VerticalContentAlignment.
        var constructor = GuiSource.MethodBody(GuiSource.ViewText("WelcomeOverlay"), "WelcomeOverlay");
        int start = constructor.IndexOf("_content = new StackPanel", StringComparison.Ordinal);
        int end = constructor.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Center", constructor[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_uses_actual_window_viewport_instead_of_an_oversized_shell_layout()
    {
        var source = GuiSource.ViewText("WelcomeOverlay");
        var bounds = GuiSource.MethodBody(source, "ApplyViewportBounds");
        Assert.Contains("Width = viewportWidth", bounds, StringComparison.Ordinal);
        Assert.Contains("Height = viewportHeight", bounds, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left", bounds, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Top", bounds, StringComparison.Ordinal);
        Assert.Contains("sender.Size", GuiSource.MethodBody(source, "OnViewportChanged"), StringComparison.Ordinal);
        Assert.Contains("UnbindViewportRoot", GuiSource.MethodBody(source, "Dispose"), StringComparison.Ordinal);
    }

    [Fact]
    public void GUI_brand_uses_native_type_instead_of_blocky_art()
    {
        var source = GuiSource.ViewText("WelcomeOverlay");
        Assert.Contains("RuUiFontFamily", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuMonoFontFamily", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SvgImageSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_giantGlyph", source, StringComparison.Ordinal);
        Assert.Contains("Open app", source, StringComparison.Ordinal);
    }

    private static int CanvasZIndex(XElement element) =>
        GuiSource.IntAttribute(element, "ZIndex");
}
