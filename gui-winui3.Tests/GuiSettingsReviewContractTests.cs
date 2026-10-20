using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class GuiSettingsReviewContractTests
{
    [Fact]
    public async Task Settings_load_retains_the_hidden_cloudflare_account_for_form_hydration()
    {
        var presenter = new SettingsPresenter();
        var settings = new RuApp.Models.AppSettingsDto { CloudflareAccountId = "account-123" };
        long request = presenter.BeginLoad();

        var result = await presenter.LoadAsync(request, () => Task.FromResult<RuApp.Models.AppSettingsDto?>(settings));

        Assert.Equal("account-123", result?.Settings.CloudflareAccountId);
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
