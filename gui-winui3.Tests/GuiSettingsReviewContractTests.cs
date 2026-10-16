using Xunit;

namespace RuApp.Tests;

public sealed class GuiSettingsReviewContractTests
{
    [Fact]
    public void Settings_hydrates_the_hidden_cloudflare_account_before_any_autosave()
    {
        string load = GuiSource.MethodBody(GuiSource.ViewText("SettingsView"), "LoadSettingsAsync");
        Assert.Contains("CloudflareAccountIdBox.Text = settings.CloudflareAccountId", load, StringComparison.Ordinal);
    }

    [Fact]
    public void Modal_cycles_keyboard_focus_and_disables_background_controls()
    {
        string shell = GuiSource.ViewText("MainWindow");
        Assert.Contains("ShellInteractionHost.IsEnabled = false", GuiSource.MethodBody(shell, "OpenSettingsPopup"), StringComparison.Ordinal);
        Assert.Contains("ShellInteractionHost.IsEnabled = true", GuiSource.MethodBody(shell, "CloseSettingsPopup"), StringComparison.Ordinal);
        var overlay = GuiSource.Require(GuiSource.Xml("MainWindow.xaml"), "SettingsModalOverlay");
        Assert.Equal("Cycle", (string?)overlay.Attribute("TabFocusNavigation"));
    }
}
