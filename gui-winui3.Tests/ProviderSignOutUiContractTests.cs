using System;
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
    public void The_active_provider_can_be_signed_out_from_the_panel()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ModelsDevSignOutButton");
        string handler = GuiSource.AttributeValue(button, "Click") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(handler), "the sign-out button must reach a handler");

        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        Assert.True(GuiSource.HasMember(code, handler), $"Click=\"{handler}\" has no member.");
        string body = GuiSource.MethodBody(code, handler);
        Assert.Contains("RemoveProviderAsync", body, StringComparison.Ordinal);
        Assert.Contains("_providerChoice", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_out_button_only_appears_while_a_key_is_stored()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string visibility = GuiSource.MethodBody(code, "UpdateVisibilityForBackend");
        Assert.Contains("ModelsDevSignOutButton", visibility, StringComparison.Ordinal);
        Assert.Contains("IsActiveProviderSignedIn", visibility, StringComparison.Ordinal);

        string signedIn = GuiSource.MethodBody(code, "IsActiveProviderSignedIn");
        Assert.Contains("_modelsDevKeyIds", signedIn, StringComparison.Ordinal);
        Assert.Contains("HasCustomKey", signedIn, StringComparison.Ordinal);
    }
}
