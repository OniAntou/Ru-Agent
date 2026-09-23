using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The path from "this provider has no key" to "the screen that fixes it".
///
/// The message itself is asserted where it is composed
/// (<see cref="ProviderSetupGuidanceTests"/>). What is asserted here is the seam:
/// that the transcript can carry an action at all, that the button exists in the
/// markup and is wired to a real handler, and that the handler reaches the window
/// that owns the settings surface. A double link in that chain is invisible in a
/// unit test of either end and is exactly what a source contract catches.
/// </summary>
public sealed class ProviderSetupActionSourceContractTests
{
    /// <summary>
    /// The action button within an already loaded document.
    /// </summary>
    /// <remarks>
    /// Takes the document rather than loading its own. GuiSource.Xml parses a fresh
    /// tree on every call, and IsDescendantOf compares by reference, so a button read
    /// from one parse is never a descendant of a template read from another.
    ///
    /// Found by AutomationProperties.AutomationId rather than x:Name: the button
    /// carries no code-behind field, and an x:Name added only for a test would be a
    /// name the product does not otherwise need.
    /// </remarks>
    private static System.Xml.Linq.XElement ActionButton(System.Xml.Linq.XDocument document) =>
        document.Descendants()
            .Where(element =>
                GuiSource.AttributeValue(element, "AutomationId") == "ChatMessageActionButton")
            .First();

    [Fact]
    public void The_transcript_can_carry_an_action()
    {
        var button = ActionButton(GuiSource.Xml("Views", "ChatView.xaml"));

        // Bound to the message, not to a shared piece of view state: each failure
        // decides for itself whether it offers a way out.
        Assert.Equal("{x:Bind ActionLabel, Mode=OneWay}", GuiSource.AttributeValue(button, "Content"));
        Assert.Equal("{x:Bind ActionKind, Mode=OneWay}", GuiSource.AttributeValue(button, "Tag"));
        Assert.Equal(
            "{x:Bind HasAction, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            GuiSource.AttributeValue(button, "Visibility"));
    }

    [Fact]
    public void The_action_button_is_inside_the_assistant_transcript_template()
    {
        // A button outside the template would render once, detached from any message.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var button = ActionButton(document);

        // x:Key is in the x: namespace, which AttributeValue deliberately skips, so
        // the key is read through the namespace directly.
        var template = document.Descendants()
            .FirstOrDefault(element =>
                (string?)element.Attribute(GuiSource.Xaml + "Key") == "AssistantMessageTemplate");

        Assert.NotNull(template);
        Assert.True(GuiSource.IsDescendantOf(button, template));
    }

    [Fact]
    public void The_button_is_wired_to_a_handler_that_exists()
    {
        var button = ActionButton(GuiSource.Xml("Views", "ChatView.xaml"));
        string handler = GuiSource.AttributeValue(button, "Click") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(handler));

        // A Click that names a missing member compiles but does nothing when pressed.
        string code = GuiSource.ViewText("ChatView");
        Assert.True(GuiSource.HasMember(code, handler), $"Click=\"{handler}\" has no member in the code-behind.");
    }

    [Fact]
    public void The_handler_dispatches_on_the_shared_action_identifier()
    {
        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "OnMessageActionClick");

        // The label is copy and may be reworded; the action identifier is the
        // contract between the message and the handler.
        Assert.Contains("OpenProviderSettingsAction", body, StringComparison.Ordinal);

        // The literal itself must come from the shared module rather than being
        // re-typed here, or the two halves can drift apart silently.
        Assert.DoesNotContain("open_provider_settings", GuiSource.StringLiterals(body));
    }

    [Fact]
    public void Raising_the_request_reaches_the_window()
    {
        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "OnMessageActionClick");

        Assert.Contains("ProviderSettingsRequested", body, StringComparison.Ordinal);

        // The window is the only place that owns the settings surface, so the
        // transcript asks it rather than opening settings itself.
        string window = GuiSource.ViewText("MainWindow");
        Assert.Contains("ProviderSettingsRequested +=", window, StringComparison.Ordinal);
        Assert.True(GuiSource.HasMember(window, "OnProviderSettingsRequested"));
    }

    [Fact]
    public void The_window_opens_settings_on_the_provider_section()
    {
        string window = GuiSource.ViewText("MainWindow");
        string body = GuiSource.MethodBody(window, "OnProviderSettingsRequested");

        // Opening settings is not enough: the user lands on whichever tab was last
        // shown, which for a first-run failure is the general tab.
        Assert.Contains("OpenSettingsPopup", body, StringComparison.Ordinal);
        Assert.Contains("ShowProviders", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_section_is_reachable_from_outside_the_settings_view()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        Assert.True(
            GuiSource.HasMember(code, "ShowProviders"),
            "SettingsView must expose a way to open the provider section.");

        // It must reuse the tab click rather than reimplement the switch, or a deep
        // link skips the catalog refresh and shows stale providers.
        string body = GuiSource.MethodBody(code, "ShowProviders");
        Assert.Contains("OnNavModelsClick", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_key_failure_offers_the_action()
    {
        string code = GuiSource.ViewText("ChatView");

        // The detection and the offer have to be in the same branch: showing the
        // message without the button, or the button without the message, both leave
        // the user with nowhere to go.
        string body = GuiSource.MethodBody(code, "ShowAgentError");

        Assert.Contains("ProviderSetupGuidance.IsMissingKey", body, StringComparison.Ordinal);
        Assert.Contains("MissingKeyMessage", body, StringComparison.Ordinal);
        Assert.Contains("ShowProviderSetupCard", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_carries_the_shared_action_rather_than_its_own()
    {
        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "ShowProviderSetupCard");

        Assert.Contains("ActionKind", body, StringComparison.Ordinal);
        Assert.Contains("OpenProviderSettingsAction", body, StringComparison.Ordinal);
        Assert.DoesNotContain("open_provider_settings", GuiSource.StringLiterals(body));
    }
}
