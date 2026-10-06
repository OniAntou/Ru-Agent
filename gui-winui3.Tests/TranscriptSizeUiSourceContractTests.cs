using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Conversation text size, end to end through the markup.
/// </summary>
/// <remarks>
/// The service and the store are asserted by
/// <see cref="TranscriptFontSizeTests"/>; the range is asserted by
/// <see cref="TranscriptSizePickerProjectionTests"/>. What is asserted here is the
/// wiring, which is where a feature like this silently does nothing: a binding that
/// resolves to nothing leaves every size looking identical, and a picker whose rows
/// are never built shows an empty menu.
/// </remarks>
public sealed class TranscriptSizeUiSourceContractTests
{
    [Fact]
    public void The_transcript_text_elements_are_addressable_by_name()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        // The size is applied from code when a row's template is realised, so the
        // elements have to be findable. Two message surfaces: the segmented reply and
        // the legacy block a failure card renders into.
        Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(GuiSource.Xaml + "Name") == "AgentLegacyTextBlock");
        Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(GuiSource.Xaml + "Name") == "AgentReplyTextBlock");

        // The activity line lives in the activity template.
        var activity = document.Descendants()
            .Where(element => (string?)element.Attribute(GuiSource.Xaml + "Name") == "AgentActivityText")
            .First();

        var template = document.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(GuiSource.Xaml + "Key") == "AgentActivityTemplate");

        Assert.NotNull(template);
        Assert.True(GuiSource.IsDescendantOf(activity, template));
    }

    [Fact]
    public void No_font_size_binding_asks_a_template_to_resolve_a_resource()
    {
        // The crash this replaced: a StaticResource lookup inside a DataTemplate for a
        // resource registered after the markup was compiled, which surfaced as
        // E_XAMLPARSEFAILED as soon as a message rendered.
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        foreach (var element in document.Descendants())
        {
            string? fontSize = GuiSource.AttributeValue(element, "FontSize");
            if (fontSize is null || !fontSize.Contains("FontSize", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("StaticResource", fontSize, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_code_applies_both_sizes_from_the_shared_instance()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.True(GuiSource.HasMember(code, "ApplyTranscriptTypography"));
        string body = GuiSource.MethodBody(code, "ApplyTranscriptTypography");

        Assert.Contains("TranscriptTypography.Instance", body, StringComparison.Ordinal);
        Assert.Contains("MessageFontSize", body, StringComparison.Ordinal);
        Assert.Contains("ActivityFontSize", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composer_takes_its_size_from_the_shared_typography()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var composer = GuiSource.Require(document, "ChatInputTextBox");

        // A second declared size here would drift from the transcript; the
        // composer follows the same shared instance as the messages.
        Assert.Null(GuiSource.AttributeValue(composer, "FontSize"));

        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "RefreshTranscriptTypography");

        Assert.Contains("ChatInputTextBox.FontSize", body, StringComparison.Ordinal);
        Assert.Contains("TranscriptTypography.Instance.MessageFontSize", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_realised_row_applies_the_size_when_its_template_loads()
    {
        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "OnMessageColumnLoaded");

        // Without this the size would only apply to rows created after a change.
        Assert.Contains("ApplyTranscriptTypography", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_specimen_strip_lives_inline_in_settings()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");

        // No dropdown, no flyout, no button label: the sizes sit in the open as
        // four equal columns stretching the full width.
        var panel = GuiSource.Require(document, "TranscriptSizeOptionsPanel");

        Assert.Equal("Grid", panel.Name.LocalName);
        Assert.Equal("*,*,*,*", GuiSource.AttributeValue(panel, "ColumnDefinitions"));
        Assert.Throws<Xunit.Sdk.XunitException>(
            () => GuiSource.Require(document, "TranscriptSizeButtonLabel"));
    }

    [Fact]
    public void The_settings_view_builds_the_specimens_and_reads_the_stored_size()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        Assert.True(GuiSource.HasMember(code, "InitTranscriptSizeOptions"));

        string constructor = GuiSource.MethodBody(code, "SettingsView");
        Assert.Contains("InitTranscriptSizeOptions", constructor, StringComparison.Ordinal);

        string init = GuiSource.MethodBody(code, "InitTranscriptSizeOptions");
        Assert.Contains("TranscriptSizePickerProjection.Sizes", init, StringComparison.Ordinal);
        Assert.Contains("TranscriptSizeOptionsPanel.Children.Add", init, StringComparison.Ordinal);
        Assert.Contains("LabelWithActivity", init, StringComparison.Ordinal);

        // No radio bullets: each specimen is a plain button, and the choice
        // reads through the accent wash applied after every build and click.
        Assert.Contains("new Button", init, StringComparison.Ordinal);
        Assert.DoesNotContain("RadioButton", init, StringComparison.Ordinal);
        Assert.Contains("RefreshTranscriptSizeSpecimens", init, StringComparison.Ordinal);

        string click = GuiSource.MethodBody(code, "OnTranscriptSizeOptionClick");
        Assert.Contains("RefreshTranscriptSizeSpecimens", click, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_size_updates_the_shared_typography()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnTranscriptSizeOptionClick");

        // Writing the store directly would leave the open transcript at the old size
        // until the app restarted.
        Assert.Contains("TranscriptTypography.Instance.MessageFontSize", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_typography_instance_is_registered_before_any_view_loads()
    {
        string code = GuiSource.Text("App.xaml.cs");
        string constructor = GuiSource.MethodBody(code, "App");

        // The transcript resolves the resource while loading, so a late registration
        // means every binding silently falls back to its default.
        Assert.Contains("RegisterTranscriptTypography", constructor, StringComparison.Ordinal);
        Assert.True(GuiSource.HasMember(code, "RegisterTranscriptTypography"));

        string registration = GuiSource.MethodBody(code, "RegisterTranscriptTypography");
        Assert.Contains("TranscriptTypography.Instance", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void The_typography_resource_is_not_declared_in_markup()
    {
        // Declaring it in App.xaml would construct a second instance beside the
        // singleton the transcript reads, and markup cannot construct this type at
        // all: its constructor takes an optional store, which the XAML compiler does
        // not accept as a default constructor.
        string markup = GuiSource.Text("App.xaml");

        Assert.DoesNotContain("TranscriptTypography", markup, StringComparison.Ordinal);
    }
}
