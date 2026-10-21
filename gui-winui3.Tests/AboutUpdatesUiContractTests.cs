using Xunit;

namespace RuApp.Tests;

public sealed class AboutUpdatesUiContractTests
{
    [Fact]
    public void About_owns_app_information_and_update_controls()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var about = GuiSource.Require(document, "PanelAboutScrollViewer");
        var general = GuiSource.Require(document, "PanelGeneralScrollViewer");
        GuiSource.Require(document, "NavAboutBtn");
        foreach (var name in new[] { "AboutVersionText", "AboutBuildText", "AboutPlatformText", "UpdateChannelCombo", "CheckUpdatesButton", "InstallUpdateButton" })
        {
            var control = GuiSource.Require(document, name);
            Assert.Contains(about, control.Ancestors());
            Assert.DoesNotContain(general, control.Ancestors());
        }
        Assert.Contains("SettingsTab.About", GuiSource.MethodBody(GuiSource.ViewText("SettingsView"), "ShowUpdates"), StringComparison.Ordinal);
    }

    [Fact]
    public void Title_bar_update_is_hidden_until_availability_is_reported()
    {
        // The button's visibility is the shell's availability flag, which starts false
        // (ShellViewModelTests.The_title_bar_entries_follow_settings_updates_and_the_side_panel).
        var button = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "TitleBarUpdateButton");
        Assert.Equal("{x:Bind Shell.IsUpdateAvailable, Mode=OneWay}", GuiSource.AttributeValue(button, "Visibility"));
        Assert.Equal("{x:Bind Shell.IsSettingsReady, Mode=OneWay}", GuiSource.AttributeValue(button, "IsEnabled"));
        var settings = GuiSource.ViewText("SettingsView");
        var publish = GuiSource.MethodBody(settings, "SetAvailableUpdate");
        Assert.Contains("UpdateAvailabilityChanged", publish, StringComparison.Ordinal);
        Assert.Contains("release is not null", publish, StringComparison.Ordinal);
        var startup = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "InitializeAppAsync");
        Assert.Contains("UpdateAvailabilityChanged += Shell.SetUpdateAvailable", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_checks_are_bounded_and_stop_with_the_settings_lifetime()
    {
        var code = GuiSource.ViewText("SettingsView");
        var start = GuiSource.MethodBody(code, "StartUpdateAvailabilityChecks");
        Assert.Contains("_installedRelease is null", start, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(30)", start, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdates", start, StringComparison.Ordinal);
        Assert.Contains("StopUpdateAvailabilityChecks", GuiSource.MethodBody(code, "Dispose"), StringComparison.Ordinal);
        var check = GuiSource.MethodBody(code, "OnCheckUpdatesClick");
        Assert.Contains("CancelAfter(TimeSpan.FromSeconds(30))", check, StringComparison.Ordinal);
        Assert.Contains("UpdateChannelCombo.SelectedIndex", check, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateLauncher", check, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateMaintenance", check, StringComparison.Ordinal);
    }
}
