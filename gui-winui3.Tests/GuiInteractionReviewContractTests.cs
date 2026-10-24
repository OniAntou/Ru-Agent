using Xunit;

namespace RuApp.Tests;

// Authored layout and native lifecycle guards. These do not replace native
// acceptance for transition frames, focus scrolling or OS preference delivery.
public sealed class GuiInteractionReviewContractTests
{
    [Fact]
    public void Settings_navigation_has_a_scroll_host_for_overflowing_categories()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var buttons = GuiSource.Require(document, "SettingsNavigationButtons");
        Assert.Contains(buttons.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        var layout = GuiSource.Text("Views", "SettingsView.Layout.cs");
        Assert.Contains("SettingsNavigationScroll.HorizontalScrollBarVisibility", layout, StringComparison.Ordinal);
        Assert.Contains("SettingsNavigationScroll.VerticalScrollBarVisibility", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_palette_shrinks_inside_its_viewport_instead_of_requesting_a_fixed_width()
    {
        var card = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "CommandPaletteCard");
        Assert.Null(card.Attribute("Width"));
        Assert.Equal("620", (string?)card.Attribute("MaxWidth"));
        Assert.Equal("Stretch", (string?)card.Attribute("HorizontalAlignment"));
    }

    [Fact]
    public void Composer_remove_controls_keep_a_useful_pointer_target()
    {
        var buttons = GuiSource.Xml("Views", "ChatView.xaml").Descendants()
            .Where(element => element.Name.LocalName == "Button"
                && (string?)element.Attribute("Click") is "OnRemoveQueuedTurnClick" or "OnRemoveAttachmentClick")
            .ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.True(GuiSource.IntAttribute(button, "Width") >= 28);
            Assert.True(GuiSource.IntAttribute(button, "Height") >= 28);
        });
    }

    [Fact]
    public void Session_motion_observes_preference_changes_and_releases_the_subscription()
    {
        var shell = GuiSource.ViewText("MainWindow");
        Assert.Contains("InitializeSessionTitleMotion", GuiSource.MethodBody(shell, "MainWindow"), StringComparison.Ordinal);
        var wave = GuiSource.Text("MainWindow.SessionWave.cs");
        Assert.Contains("AnimationsEnabledChanged +=", wave, StringComparison.Ordinal);
        Assert.Contains("AnimationsEnabledChanged -=", GuiSource.MethodBody(wave, "StopSessionTitleWaves"), StringComparison.Ordinal);
        var change = GuiSource.MethodBody(wave, "OnSessionAnimationsChanged");
        Assert.Contains("TryEnqueue", change, StringComparison.Ordinal);
        Assert.Contains("RefreshSessionMotionPreference", change, StringComparison.Ordinal);
        Assert.Contains("RefreshSessionTitleWaves", GuiSource.MethodBody(wave, "RefreshSessionMotionPreference"), StringComparison.Ordinal);
        Assert.Contains("IsWindowsVersionAtLeast", GuiSource.MethodBody(wave, "InitializeSessionTitleMotion"), StringComparison.Ordinal);
        Assert.Contains("_sessionMotionPoll?.Stop()", GuiSource.MethodBody(wave, "StopSessionTitleWaves"), StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_reversal_reads_presented_values_before_stopping_the_storyboard()
    {
        var source = GuiSource.Text("Controls", "AgentSidePanelControl.Motion.cs");
        var transition = GuiSource.MethodBody(source, "PlaySurfaceTransition");
        int stop = transition.IndexOf("CancelVisibilityAnimations();", StringComparison.Ordinal);
        Assert.True(stop > 0);
        Assert.Contains("host.Opacity", transition[..stop], StringComparison.Ordinal);
        Assert.Contains("slide.X", transition[..stop], StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_visibilityHost, host)", transition[..stop], StringComparison.Ordinal);
        Assert.Contains("PlayVisibilityEntrance()", GuiSource.MethodBody(source, "CancelVisibilityExit"), StringComparison.Ordinal);
    }
}
