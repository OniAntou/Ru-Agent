using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Title bar contract: the icon buttons hover as an inset rounded plate and draw their
/// own focus ring (no system focus rectangle), and back/forward sit beside the sidebar
/// toggle, record every visit once and replay through the ordinary navigation paths.
/// </summary>
public sealed class TitleBarHistoryContractTests
{
    [Fact]
    public void Icon_buttons_hover_as_an_inset_rounded_plate_and_draw_their_own_focus_ring()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var style = document.Descendants()
            .First(element => GuiSource.AttributeValue(element, "Key") == "TitleBarIconButtonStyle"
                || (string?)element.Attribute(GuiSource.Xaml + "Key") == "TitleBarIconButtonStyle");
        System.Xml.Linq.XElement Named(string name) => style.Descendants()
            .First(element => (string?)element.Attribute(GuiSource.Xaml + "Name") == name);
        var plate = Named("Plate");
        Named("Ring");
        Assert.Equal("6", GuiSource.AttributeValue(plate, "CornerRadius"));
        Assert.Equal("2", GuiSource.AttributeValue(plate, "Margin"));

        var text = style.ToString();
        Assert.Contains("Plate.Background", text, StringComparison.Ordinal);
        Assert.Contains("Ring.Opacity", text, StringComparison.Ordinal);
        Assert.Contains("BrushTransition", text, StringComparison.Ordinal);
        Assert.Contains(
            style.Descendants(),
            element => element.Name.LocalName == "Setter"
                && GuiSource.AttributeValue(element, "Property") == "UseSystemFocusVisuals"
                && GuiSource.AttributeValue(element, "Value") == "False");
        // The old full-slot fill is gone: the root paints nothing itself.
        Assert.DoesNotContain("Root.Background", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Back_and_forward_sit_beside_the_toggle_and_start_disabled()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var panel = GuiSource.Require(document, "TitleBarHistory");
        Assert.Same(GuiSource.Require(document, "AppTitleBar"), panel.Parent);
        var back = GuiSource.Require(document, "HistoryBackButton");
        var forward = GuiSource.Require(document, "HistoryForwardButton");
        Assert.Same(panel, back.Parent);
        Assert.Same(panel, forward.Parent);
        Assert.Equal("Back", GuiSource.AttributeValue(back, "AutomationProperties.Name"));
        Assert.Equal("Forward", GuiSource.AttributeValue(forward, "AutomationProperties.Name"));
        Assert.Equal("False", GuiSource.AttributeValue(back, "IsEnabled"));
        Assert.Equal("False", GuiSource.AttributeValue(forward, "IsEnabled"));
        Assert.Equal("OnHistoryBackClick", GuiSource.AttributeValue(back, "Click"));
        Assert.Equal("OnHistoryForwardClick", GuiSource.AttributeValue(forward, "Click"));
    }

    [Fact]
    public void The_three_title_bar_buttons_keep_their_sizes_and_share_one_icon_cell()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var toggle = GuiSource.Require(document, "NavigationSidebarToggleButton");
        var back = GuiSource.Require(document, "HistoryBackButton");
        var forward = GuiSource.Require(document, "HistoryForwardButton");
        // The toggle keeps the style's own 46 (no override); back and forward are 36.
        Assert.Null(toggle.Attribute("Width"));
        Assert.Equal("36", GuiSource.AttributeValue(back, "Width"));
        Assert.Equal("36", GuiSource.AttributeValue(forward, "Width"));
        // The toggle keeps its original Viewbox drawing (its glyph is pinned to the side
        // panel's). Back and forward share one 16x16 cell drawn at natural size, so they
        // sit on one centre line with it.
        Assert.Contains(toggle.Descendants(), element => element.Name.LocalName == "Viewbox");
        foreach (var button in new[] { back, forward })
        {
            Assert.DoesNotContain(button.Descendants(), element => element.Name.LocalName == "Viewbox");
            var cell = Assert.Single(button.Elements(), element => element.Name.LocalName == "Canvas");
            Assert.Equal("16", GuiSource.AttributeValue(cell, "Width"));
            Assert.Equal("16", GuiSource.AttributeValue(cell, "Height"));
        }

        // Back and forward tuck 6 px into the toggle's empty slot margin so the icons sit
        // about as far from the toggle's as from each other.
        var code = GuiSource.ViewText("MainWindow");
        Assert.Contains("HistoryOverlap = 6", code, StringComparison.Ordinal);
        Assert.Contains("- HistoryOverlap", GuiSource.MethodBody(code, "UpdateTitleBarToggleLayout"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_bar_registers_a_passthrough_region_and_dims_with_the_window()
    {
        var code = GuiSource.ViewText("MainWindow");
        var region = GuiSource.MethodBody(code, "ApplySidePanelToggleRegion");
        Assert.Contains("RegionFor(TitleBarHistory)", region, StringComparison.Ordinal);
        Assert.Contains("regions.Add(middle)", region, StringComparison.Ordinal);
        var layout = GuiSource.MethodBody(code, "UpdateTitleBarToggleLayout");
        Assert.Contains("TitleBarHistory.Margin", layout, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeOpacity(TitleBarHistory", GuiSource.MethodBody(code, "OnWindowActivatedForSidePanelToggle"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_way_of_arriving_records_once_and_a_history_move_does_not_record_itself()
    {
        var code = GuiSource.ViewText("MainWindow");
        var apply = GuiSource.MethodBody(code, "ApplyNavigation");
        Assert.Contains("RecordLocation(navigation.Destination", apply, StringComparison.Ordinal);
        // Seeded before the chat is left, while its session is still known.
        Assert.True(
            apply.IndexOf("SeedHistoryWithTheChatBeingLeft()", StringComparison.Ordinal)
            < apply.IndexOf("LeaveChatSession()", StringComparison.Ordinal));
        Assert.Contains("RecordLocation(ShellDestination.Chat, e.SessionId)", GuiSource.MethodBody(code, "OnChatSessionOpened"), StringComparison.Ordinal);

        var record = GuiSource.MethodBody(code, "RecordLocation");
        Assert.Contains("_applyingHistory", record, StringComparison.Ordinal);
        Assert.Contains("UpdateHistoryButtons()", record, StringComparison.Ordinal);

        var move = GuiSource.MethodBody(code, "MoveInHistoryAsync");
        Assert.Contains("_history.TryMove(direction, CanReturnTo", move, StringComparison.Ordinal);
        Assert.Contains("_applyingHistory = true", move, StringComparison.Ordinal);
        Assert.Contains("finally", move, StringComparison.Ordinal);
        Assert.Contains("OpenSessionFromPaletteAsync", move, StringComparison.Ordinal);
        Assert.Contains("ShellRoutePolicy.TagFor", move, StringComparison.Ordinal);
    }
}
