using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The connection test's seam: markup, handler, and what the handler does.
/// </summary>
/// <remarks>
/// The wording is asserted in
/// <see cref="ProviderConnectionTestPresentationTests"/>. What is asserted here is
/// that the button exists, that pressing it reaches a real handler, and that the
/// handler saves before it asks -- the ordering that decides whether the test proves
/// the key the user just typed or the one before it.
/// </remarks>
public sealed class ProviderConnectionTestUiSourceContractTests
{
    [Fact]
    public void The_test_button_and_its_status_row_exist_in_the_provider_section()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");

        var button = GuiSource.Require(document, "ProviderTestButton");
        var status = GuiSource.Require(document, "ProviderTestStatusText");
        var icon = GuiSource.Require(document, "ProviderTestStatusIcon");

        // Both live in the models panel. A status line outside the section the button
        // is in would be read as belonging to something else.
        var modelsPanel = GuiSource.Require(document, "PanelModelsScrollViewer");
        Assert.True(GuiSource.IsDescendantOf(button, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(status, modelsPanel));
        Assert.True(GuiSource.IsDescendantOf(icon, modelsPanel));
    }

    [Fact]
    public void The_status_carries_a_tone_icon_and_keeps_the_raw_code_out_of_the_text()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "ShowProviderTestResult");

        Assert.Contains("ProviderTestStatusIcon", body, StringComparison.Ordinal);
        Assert.Contains("SetToolTip", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[{code}]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_is_announced_when_it_changes()
    {
        // The result appears without focus moving, so a screen reader has to be told.
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var status = GuiSource.Require(document, "ProviderTestStatusText");

        Assert.Equal("Polite", GuiSource.AttributeValue(status, "LiveSetting"));
    }

    [Fact]
    public void The_button_is_wired_to_a_handler_that_exists()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");
        var button = GuiSource.Require(document, "ProviderTestButton");
        string handler = GuiSource.AttributeValue(button, "Click") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(handler));

        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        Assert.True(GuiSource.HasMember(code, handler), $"Click=\"{handler}\" has no member.");
    }

    [Fact]
    public void The_handler_saves_before_it_asks()
    {
        // Auto-save is debounced, so the backend would still hold the previous key.
        // This ordering is the difference between proving the new key and proving the
        // old one, and it is invisible in the markup.
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnProviderTestClick");

        int save = body.IndexOf("SaveSettingsAsync", StringComparison.Ordinal);
        int refresh = body.IndexOf("RefreshAsync", StringComparison.Ordinal);

        Assert.True(save >= 0, "the handler must save the pending settings");
        Assert.True(refresh >= 0, "the handler must force a catalog refresh");
        Assert.True(save < refresh, "saving must happen before the refresh, or the old key is tested");
    }

    [Fact]
    public void The_handler_uses_the_shared_presentation_rather_than_its_own_copy()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnProviderTestClick");

        Assert.Contains("ProviderConnectionTestPresentation", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_test_records_its_catalog_code()
    {
        // The message alone cannot tell a blocked network apart from a bad key
        // the next time someone reads a report, so the code must reach both the
        // status line and the local diagnostic log.
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnProviderTestClick");

        Assert.Contains("WriteGuiError", body, StringComparison.Ordinal);
        Assert.Contains("ErrorCode", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_background_success_never_overwrites_the_status_line()
    {
        // The catalog also refreshes on a timer. A result appearing without the button
        // being pressed would read as the user's own test.
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnProviderCatalogStateChanged");

        Assert.Contains("ReportsFailure", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalog_subscription_is_released_when_settings_goes_away()
    {
        // The controller outlives the view when the window owns it, so an unremoved
        // handler would keep calling into a torn-down view.
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        string initialize = GuiSource.MethodBody(code, "Initialize");
        Assert.Contains("StateChanged +=", initialize, StringComparison.Ordinal);

        string dispose = GuiSource.MethodBody(code, "DisposeEventHandlers");
        Assert.Contains("StateChanged -=", dispose, StringComparison.Ordinal);
    }
}
