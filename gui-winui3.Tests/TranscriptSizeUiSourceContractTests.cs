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
    public void The_transcript_binds_its_message_size_to_the_shared_instance()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var bound = document.Descendants()
            .Where(element =>
                (GuiSource.AttributeValue(element, "FontSize") ?? string.Empty)
                    .Contains("MessageFontSize", StringComparison.Ordinal))
            .ToArray();

        // Both transcript text surfaces: the segmented reply and the legacy block a
        // failure card is rendered into. A hardcoded size on either leaves half the
        // conversation ignoring the setting.
        Assert.Equal(2, bound.Length);
    }

    [Fact]
    public void The_activity_line_binds_to_the_smaller_size()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var activity = document.Descendants()
            .FirstOrDefault(element =>
                (GuiSource.AttributeValue(element, "Key") is null)
                && (GuiSource.AttributeValue(element, "FontSize") ?? string.Empty)
                    .Contains("ActivityFontSize", StringComparison.Ordinal));

        Assert.NotNull(activity);

        // The activity line must be inside the activity template, not the message one:
        // binding a message row to the smaller size would shrink the wrong text.
        var template = document.Descendants()
            .FirstOrDefault(element =>
                (string?)element.Attribute(GuiSource.Xaml + "Key") == "AgentActivityTemplate");

        Assert.NotNull(template);
        Assert.True(GuiSource.IsDescendantOf(activity, template));
    }

    [Fact]
    public void The_size_is_read_through_a_binding_that_can_change()
    {
        // x:Bind is compiled once per template and cannot follow a value that changes
        // at runtime, so these bindings must be classic bindings with the shared
        // instance as their source.
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        foreach (var element in document.Descendants())
        {
            string? fontSize = GuiSource.AttributeValue(element, "FontSize");
            if (fontSize is null || !fontSize.Contains("FontSize", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains("Binding ", fontSize, StringComparison.Ordinal);
            Assert.Contains("Source={StaticResource TranscriptTypography}", fontSize, StringComparison.Ordinal);
            Assert.DoesNotContain("x:Bind", fontSize, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_picker_row_host_exists_in_settings()
    {
        var document = GuiSource.Xml("Views", "SettingsView.xaml");

        var panel = GuiSource.Require(document, "TranscriptSizeOptionsPanel");
        var label = GuiSource.Require(document, "TranscriptSizeButtonLabel");

        // Rows are added to this panel at runtime, so an empty panel is the failure
        // mode: the menu opens with nothing in it.
        Assert.Equal("StackPanel", panel.Name.LocalName);
        Assert.Equal("TextBlock", label.Name.LocalName);
    }

    [Fact]
    public void The_settings_view_builds_the_rows_and_reads_the_stored_size()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        Assert.True(GuiSource.HasMember(code, "InitTranscriptSizeOptions"));

        string constructor = GuiSource.MethodBody(code, "SettingsView");
        Assert.Contains("InitTranscriptSizeOptions", constructor, StringComparison.Ordinal);

        string init = GuiSource.MethodBody(code, "InitTranscriptSizeOptions");
        Assert.Contains("TranscriptSizePickerProjection.Sizes", init, StringComparison.Ordinal);
        Assert.Contains("TranscriptSizeOptionsPanel.Children.Add", init, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_size_updates_the_shared_typography()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "OnTranscriptSizeOptionChecked");

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
