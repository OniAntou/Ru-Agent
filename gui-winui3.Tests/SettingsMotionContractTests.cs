using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Settings motion contract: pages arrive card by card, the rail and the segmented
/// controls move instead of snapping, and every effect goes through the shared motion
/// module so it follows the Windows animation setting.
/// </summary>
public sealed class SettingsMotionContractTests
{
    private static string Code => GuiSource.ViewText("SettingsView");

    [Fact]
    public void A_tab_change_plays_the_entrance_and_a_repeat_click_does_not()
    {
        var select = GuiSource.MethodBody(Code, "SelectTab");
        Assert.Contains("_shownTab", select, StringComparison.Ordinal);
        Assert.Contains("PlayTabEntrance(tab)", select, StringComparison.Ordinal);
        Assert.Contains("previous != tab", select, StringComparison.Ordinal);

        var entrance = GuiSource.MethodBody(Code, "PlayTabEntrance");
        Assert.Contains("UiMotion.Enter(card,", entrance, StringComparison.Ordinal);
        Assert.Contains("CardStaggerMilliseconds", entrance, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeOpacity", entrance, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rail_cross_fades_its_highlight()
    {
        var highlight = GuiSource.MethodBody(Code, "HighlightNavButton");
        Assert.Contains("UiMotion.FadeBrush(button, Control.BackgroundProperty", highlight, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeBrush(button, Control.ForegroundProperty", highlight, StringComparison.Ordinal);
    }

    [Fact]
    public void Segmented_controls_glide_a_thumb_and_snap_on_resize()
    {
        var place = GuiSource.MethodBody(Code, "PlaceSegmentThumb");
        Assert.Contains("UiMotion.SlideX", place, StringComparison.Ordinal);
        // Only a thumb that was already placed glides; the first placement is a jump.
        Assert.Contains("_placedThumbs.Contains(thumb)", place, StringComparison.Ordinal);

        var init = GuiSource.MethodBody(Code, "InitializeSegmentThumbs");
        Assert.Contains("SizeChanged", init, StringComparison.Ordinal);
        Assert.Contains("animate: false", init, StringComparison.Ordinal);

        Assert.Contains("PlaceSegmentThumb(ThemeThumb)", GuiSource.MethodBody(Code, "RefreshThemeUI"), StringComparison.Ordinal);
        Assert.Contains("PlaceSegmentThumb(ActivityThumb)", GuiSource.MethodBody(Code, "SyncActivityStyleChoice"), StringComparison.Ordinal);
        Assert.Contains("PlaceSegmentThumb(UpdateChannelThumb)", GuiSource.MethodBody(Code, "SetChannelSelection"), StringComparison.Ordinal);

        // The thumb paints the selection, so a checked segment is see-through.
        var xaml = GuiSource.Xml("Views", "SettingsView.xaml");
        var thumbs = new[] { "ThemeThumb", "ActivityThumb", "UpdateChannelThumb" };
        foreach (var name in thumbs)
        {
            var thumb = GuiSource.Require(xaml, name);
            Assert.Equal("False", GuiSource.AttributeValue(thumb, "IsHitTestVisible"));
        }
    }

    [Fact]
    public void The_update_row_follows_one_state_and_its_changes_are_animated()
    {
        var set = GuiSource.MethodBody(Code, "SetUpdateState");
        Assert.Contains("UpdateStatusPresentation.Headline", set, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeOpacity(UpdateHeadlineText", set, StringComparison.Ordinal);
        foreach (var show in new[] { "ShowCheck", "ShowInstall", "ShowCancel", "ShowProgress" })
            Assert.Contains("UpdateStatusPresentation." + show, set, StringComparison.Ordinal);
        Assert.Contains("UiMotion.SetVisible(element, visible)", GuiSource.MethodBody(Code, "ShowUpdateElement"), StringComparison.Ordinal);

        // The channel is locked while a check or a download runs, and says so by fading.
        Assert.Contains("UiMotion.FadeOpacity(UpdateChannelChoice", GuiSource.MethodBody(Code, "SetChannelEnabled"), StringComparison.Ordinal);

        // Every outcome goes through the state, never straight to the headline.
        var check = GuiSource.MethodBody(Code, "CheckUpdateAvailabilityAsync");
        foreach (var state in new[] { "Checking", "Available", "UpToDate", "Failed" })
            Assert.Contains("UpdateUiState." + state, check, StringComparison.Ordinal);
        var install = GuiSource.MethodBody(Code, "OnInstallUpdateClick");
        foreach (var state in new[] { "Downloading", "Installing", "Cancelled", "Available" })
            Assert.Contains("UpdateUiState." + state, install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_full_access_notice_enters_through_the_shared_motion()
    {
        var preset = GuiSource.MethodBody(Code, "RefreshPresetUI");
        Assert.Contains("UiMotion.SetVisible(FullAccessNotice", preset, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_filters_cards_and_lets_filtered_cards_return_with_the_entrance()
    {
        var apply = GuiSource.MethodBody(Code, "ApplySettingsSearch");
        Assert.Contains("UiMotion.SetVisible(entry.Card", apply, StringComparison.Ordinal);
        Assert.Contains("SettingsSearch.Matches", apply, StringComparison.Ordinal);
        Assert.Contains("NavigateTo(first!.Value)", apply, StringComparison.Ordinal);
        Assert.Contains("UiMotion.SetVisible(SettingsNoResultsText", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void Enter_can_hold_an_element_back_so_cards_arrive_in_sequence()
    {
        var motion = GuiSource.Text("Services", "UiMotion.cs");
        var enter = GuiSource.MethodBody(motion, "Enter");
        Assert.Contains("delayMilliseconds", enter, StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", enter, StringComparison.Ordinal);

        var delay = GuiSource.MethodBody(motion, "ApplyDelay");
        Assert.Contains("AnimationDelayBehavior.SetInitialValueBeforeDelay", delay, StringComparison.Ordinal);
        Assert.InRange(UiMotion.EntranceMilliseconds, 60, 240);
    }

    [Fact]
    public void The_rail_carries_search_and_the_Ru_group_and_both_follow_the_narrow_layout()
    {
        var xaml = GuiSource.Xml("Views", "SettingsView.xaml");
        var search = GuiSource.Require(xaml, "SettingsSearchBox");
        Assert.Equal("Search settings", GuiSource.AttributeValue(search, "AutomationProperties.Name"));
        Assert.Equal("Ru", GuiSource.AttributeValue(GuiSource.Require(xaml, "SettingsNavigationRuHeading"), "Text"));

        var layout = GuiSource.Text("Views", "SettingsView.Layout.cs");
        Assert.Contains("SettingsSearchBox.Width", layout, StringComparison.Ordinal);
        Assert.Contains("UpdateRuHeadingVisibility()", layout, StringComparison.Ordinal);
    }
}
