using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Signing out of the active catalog provider from the Models &amp; Providers
/// panel: the button removes the stored key through the settings route, and it
/// only appears while a key is actually stored.
/// </summary>
public sealed class ProviderSignOutUiContractTests
{
    [Fact]
    public void The_active_provider_sign_out_button_is_wired_in_the_panel()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ModelsDevSignOutButton");
        string handler = GuiSource.AttributeValue(button, "Click") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(handler), "the sign-out button must reach a handler");

        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        Assert.True(GuiSource.HasMember(code, handler), $"Click=\"{handler}\" has no member.");
    }
}
