using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Retains structural checks only for the WinUI connection-test boundary.
/// Save/refresh ordering is covered by SettingsProviderPresenterTests.
/// </summary>
public sealed class ProviderConnectionTestUiSourceContractTests
{
    [Fact]
    public void Test_button_and_accessible_status_stay_together_in_provider_section()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ProviderTestButton");
        var status = GuiSource.Require(document, "ProviderTestStatusText");
        var icon = GuiSource.Require(document, "ProviderTestStatusIcon");
        var modelsPanel = GuiSource.Require(document, "PanelModelsScrollViewer");

        Assert.True(GuiSource.IsDescendantOf(button, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(status, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(icon, modelsPanel));
        Assert.Equal("Polite", GuiSource.AttributeValue(status, "LiveSetting"));
    }

    [Fact]
    public void Test_button_is_wired_to_an_existing_ui_handler()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ProviderTestButton");
        string handler = GuiSource.AttributeValue(button, "Click") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(handler));
        Assert.True(GuiSource.HasMember(GuiSource.Text("Views", "SettingsView.xaml.cs"), handler));
    }

    [Fact]
    public void Settings_releases_the_long_lived_catalog_subscription_on_disposal()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        Assert.Contains("StateChanged +=", GuiSource.MethodBody(code, "Initialize"), StringComparison.Ordinal);
        Assert.Contains("StateChanged -=", GuiSource.MethodBody(code, "DisposeEventHandlers"), StringComparison.Ordinal);
    }
}
