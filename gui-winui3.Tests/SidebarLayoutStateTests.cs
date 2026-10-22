using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarLayoutStateTests
{
    [Theory]
    [InlineData(72, 220)]
    [InlineData(219, 220)]
    [InlineData(220, 220)]
    [InlineData(298, 298)]
    [InlineData(360, 360)]
    [InlineData(600, 360)]
    [InlineData(double.NaN, 250)]
    [InlineData(double.PositiveInfinity, 250)]
    public void All_resize_paths_use_a_bounded_full_width(double requested, double expected)
    {
        var state = new SidebarLayoutState();
        state.SetWidth(requested);
        Assert.Equal(expected, state.VisibleWidth);
        Assert.False(state.IsHidden);
    }

    [Fact]
    public void Hiding_removes_the_sidebar_completely_and_restores_its_last_width()
    {
        var state = new SidebarLayoutState();
        state.SetWidth(314);
        state.Toggle();
        Assert.True(state.IsHidden);
        Assert.Equal(0, state.VisibleWidth);
        Assert.Equal(314, state.ExpandedWidth);
        state.Toggle();
        Assert.False(state.IsHidden);
        Assert.Equal(314, state.VisibleWidth);
    }

    [Fact]
    public void Shell_wires_capture_cleanup_and_removes_all_rail_triggers()
    {
        var shell = GuiSource.ViewText("MainWindow");
        var xaml = GuiSource.Xml("MainWindow.xaml");
        var grip = GuiSource.Require(xaml, "SidebarResizer");
        Assert.Equal("Resizer_PointerCanceled", GuiSource.AttributeValue(grip, "PointerCanceled"));
        Assert.Equal("Resizer_PointerCaptureLost", GuiSource.AttributeValue(grip, "PointerCaptureLost"));
        Assert.Equal("Resizer_Unloaded", GuiSource.AttributeValue(grip, "Unloaded"));
        Assert.DoesNotContain("ApplySidebarVisualMode", shell);
        Assert.DoesNotContain("ConfigureMiniButton", shell);
        Assert.Equal("OnNavigationSidebarToggleClick", GuiSource.AttributeValue(
            GuiSource.Require(xaml, "NavigationSidebarToggleButton"), "Click"));
    }
}
