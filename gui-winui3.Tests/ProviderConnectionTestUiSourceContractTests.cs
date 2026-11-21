using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Retains structural checks only for the WinUI provider-models boundary.
/// Save/refresh ordering is covered by SettingsProviderPresenterTests.
/// </summary>
public sealed class ProviderConnectionTestUiSourceContractTests
{
    [Fact]
    public void Fetch_button_and_accessible_status_stay_together_in_provider_section()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "FetchModelsButton");
        var status = GuiSource.Require(document, "ProviderModelsStatusText");
        var icon = GuiSource.Require(document, "ProviderModelsStatusIcon");
        var modelsPanel = GuiSource.Require(document, "ProviderModelsSection");
        var list = GuiSource.Require(document, "ProviderModelsList");

        Assert.Equal("Fetch available models", GuiSource.AttributeValue(button, "Content"));
        Assert.True(GuiSource.IsDescendantOf(button, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(status, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(icon, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(list, modelsPanel));
        Assert.Equal("Polite", GuiSource.AttributeValue(status, "LiveSetting"));
    }

    [Fact]
    public void Fetch_button_is_wired_to_the_view_model_command()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "FetchModelsButton");

        Assert.Equal("{x:Bind ViewModel.FetchAvailableModelsCommand}", GuiSource.AttributeValue(button, "Command"));
    }
}
