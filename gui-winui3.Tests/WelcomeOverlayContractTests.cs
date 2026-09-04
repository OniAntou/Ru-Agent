using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

public sealed class WelcomeOverlayContractTests
{
    [Fact]
    public void MainWindow_xaml_contains_welcome_overlay_spanning_full_window()
    {
        string xaml = File.ReadAllText(FindSource("MainWindow.xaml"));
        Assert.Contains("controls:WelcomeOverlay", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WelcomeOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Canvas.ZIndex=\"1000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_codebehind_updates_and_dismisses_welcome_overlay()
    {
        string code = File.ReadAllText(FindSource("MainWindow.xaml.cs"));
        Assert.Contains("WelcomeOverlay?.SetStatus", code, StringComparison.Ordinal);
        Assert.Contains("WelcomeOverlay.DismissAsync()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_paths_control_implements_bezier_curves_and_disposable()
    {
        string code = File.ReadAllText(FindSource("Controls", "BackgroundPathsControl.cs"));
        Assert.Contains("PathCount = 36", code, StringComparison.Ordinal);
        Assert.Contains("CanvasWidth = 696.0", code, StringComparison.Ordinal);
        Assert.Contains("CanvasHeight = 316.0", code, StringComparison.Ordinal);
        Assert.Contains("BezierSegment", code, StringComparison.Ordinal);
        Assert.Contains("IDisposable", code, StringComparison.Ordinal);
        Assert.Contains("ActualThemeChanged", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_overlay_has_accessible_action_button_and_status()
    {
        string code = File.ReadAllText(FindSource("Controls", "WelcomeOverlay.cs"));
        Assert.Contains("AutomationProperties.SetName(_enterButton", code, StringComparison.Ordinal);
        Assert.Contains("BackgroundPathsControl", code, StringComparison.Ordinal);
        Assert.Contains("SetStatus", code, StringComparison.Ordinal);
        Assert.Contains("DismissAsync", code, StringComparison.Ordinal);
        Assert.Contains("NotifyReady", code, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string root = directory.FullName;
            if (File.Exists(Path.Combine(root, "gui-winui3", "MainWindow.xaml")))
            {
                return Path.Combine(new[] { root, "gui-winui3" }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
