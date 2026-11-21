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
    public void Test_button_is_wired_to_the_view_model_command()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ProviderTestButton");

        Assert.Equal("{x:Bind ViewModel.TestConnectionCommand}", GuiSource.AttributeValue(button, "Command"));
    }

}
