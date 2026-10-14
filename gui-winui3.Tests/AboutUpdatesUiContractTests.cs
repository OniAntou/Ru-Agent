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
        var button = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "TitleBarUpdateButton");
        Assert.Equal("Collapsed", GuiSource.AttributeValue(button, "Visibility"));
        var settings = GuiSource.ViewText("SettingsView");
        var publish = GuiSource.MethodBody(settings, "SetAvailableUpdate");
        Assert.Contains("UpdateAvailabilityChanged", publish, StringComparison.Ordinal);
        Assert.Contains("release is not null", publish, StringComparison.Ordinal);
        var handler = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "OnUpdateAvailabilityChanged");
        Assert.Contains("Visibility.Visible", handler, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", handler, StringComparison.Ordinal);
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
