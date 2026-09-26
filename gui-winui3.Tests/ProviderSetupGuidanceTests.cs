using System.Globalization;
using System.Xml.Linq;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A new user's first failure is "I sent a message and nothing worked". These
/// assert that the app can name which provider is unconfigured, say where a key
/// comes from, and — importantly — stay quiet when a setup is actually valid.
/// </summary>
public sealed class ProviderSetupGuidanceTests
{
    private static AppSettingsDto Empty() => new();

    private static AppSettingsDto WithOpenAiKey() => new() { HasOpenAiKey = true };

    [Theory]
    [InlineData("openai", "OpenAI")]
    [InlineData("anthropic", "Anthropic")]
    [InlineData("custom", "Custom endpoint")]
    public void Every_known_alias_has_a_display_name(string alias, string expected)
    {
        Assert.Equal(expected, ProviderSetupGuidance.DisplayName(alias));
    }

    [Fact]
    public void An_unknown_alias_names_itself_rather_than_showing_blank()
    {
        // An unmapped provider is the only handle a user has; blank is worse.
        Assert.Equal("some-new-provider", ProviderSetupGuidance.DisplayName("some-new-provider"));
    }

    [Fact]
    public void An_absent_alias_names_no_provider_rather_than_inventing_one()
    {
        // Provider choice is Models.dev's catalog plus the custom endpoint. An
        // absent alias has no provider behind it yet -- naming one here would put a
        // provider in the trigger that the user never selected. The catalog, not
        // this table, decides. See ResolveUnsetChoice.
        Assert.Equal("No provider", ProviderSetupGuidance.DisplayName(null));
        Assert.Equal("No provider", ProviderSetupGuidance.DisplayName("   "));
        Assert.Equal(ProviderSetupGuidance.UnsetChoice, ProviderSetupGuidance.NormalizeProviderChoice(null));
    }

    [Fact]
    public void Every_provider_names_where_its_key_is_created()
    {
        foreach (var provider in ProviderSetupGuidance.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.KeyHint));
        }
    }

    [Fact]
    public void Provider_guidance_carries_no_link()
    {
        // Settings shows the hint as text. A URL field was added and then removed
        // because nothing rendered it; this keeps a dead field from coming back.
        var properties = typeof(ProviderSetupGuidance.ProviderInfo)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("KeyUrl", properties);
    }

    [Fact]
    public void A_provider_without_a_key_is_reported_as_missing()
    {
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "openai"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "anthropic"));
    }

    [Fact]
    public void A_provider_with_a_key_is_not_reported_as_missing()
    {
        Assert.False(ProviderSetupGuidance.IsMissingKey(WithOpenAiKey(), "openai"));
    }

    [Fact]
    public void One_providers_key_does_not_satisfy_another()
    {
        // The bug this prevents: OpenAI configured, OpenAI selected, still warned.
        var settings = WithOpenAiKey();

        Assert.False(ProviderSetupGuidance.IsMissingKey(settings, "openai"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(settings, "anthropic"));
    }

    [Fact]
    public void Unreadable_settings_never_claim_a_key_is_missing()
    {
        // Sending someone to fix a key when the settings could not be read is worse
        // than saying nothing: the settings screen may already be correct.
        Assert.False(ProviderSetupGuidance.IsMissingKey(null, "openai"));
    }

    [Fact]
    public void An_unknown_provider_is_not_reported_as_missing()
    {
        // Nothing is known about it, so there is nothing to assert about its key.
        Assert.False(ProviderSetupGuidance.IsMissingKey(Empty(), "brand-new-provider"));
    }

    [Fact]
    public void A_custom_endpoint_is_never_reported_as_missing_a_key()
    {
        // A custom endpoint is whatever base URL the user pointed Ru at. Whether it
        // wants a key is a property of that endpoint, not something the app can know,
        // so it asks the endpoint through the connection test instead of guessing.
        var settings = new AppSettingsDto
        {
            CustomUrl = "https://api.deepseek.com/v1",
            HasCustomKey = false,
        };

        Assert.False(ProviderSetupGuidance.IsMissingKey(settings, "custom"));
    }

    [Fact]
    public void The_custom_provider_is_marked_as_accepting_an_optional_key()
    {
        var custom = ProviderSetupGuidance.Find("custom");

        Assert.NotNull(custom);
        Assert.True(custom.KeyIsOptional);
    }

    [Fact]
    public void Every_hosted_provider_requires_a_key()
    {
        // The counterpart to the custom rule: these two cannot answer anything
        // without a credential, so the warning has to stay for them.
        foreach (var alias in new[] { "openai", "anthropic" })
        {
            var provider = ProviderSetupGuidance.Find(alias);

            Assert.NotNull(provider);
            Assert.False(provider.KeyIsOptional);
            Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), alias));
        }
    }

    [Fact]
    public void A_custom_key_that_is_present_is_simply_not_reported()
    {
        var settings = new AppSettingsDto
        {
            CustomUrl = "https://api.example.com/v1",
            HasCustomKey = true,
        };

        Assert.False(ProviderSetupGuidance.IsMissingKey(settings, "custom"));
    }

    [Fact]
    public void The_missing_key_message_names_the_provider_and_says_where_to_go()
    {
        string message = ProviderSetupGuidance.MissingKeyMessage(Empty(), "openai");

        Assert.Contains("OpenAI", message, StringComparison.Ordinal);
        Assert.Contains("API key", message, StringComparison.Ordinal);
        // The message carries the human hint; the URL is a separate field so the
        // settings screen can offer it as a link rather than pasted text.
        Assert.Contains("OpenAI platform", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_missing_key_message_names_the_provider_instead_of_a_raw_alias()
    {
        string message = ProviderSetupGuidance.MissingKeyMessage(Empty(), "anthropic");

        Assert.Contains("Anthropic", message, StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic has no", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_settings_payload_still_produces_directions()
    {
        string message = ProviderSetupGuidance.MissingKeyMessage(null, "openai");

        Assert.Contains("OpenAI", message, StringComparison.Ordinal);
        Assert.Contains("Settings", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_picker_offers_exactly_the_fixed_aliases_it_declares()
    {
        // The fixed rows are no longer markup: the custom endpoint is built as a row by
        // the code, so the declared list and the code that uses it must agree.
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        string body = GuiSource.MethodBody(code, "RebuildModelsDevOptions");

        Assert.Contains("new(\"custom\", CustomProviderDisplayName)", body, StringComparison.Ordinal);
        Assert.Equal(["custom"], ProviderSetupGuidance.PickerAliases);
    }

    [Fact]
    public void Every_picker_row_is_a_provider_the_app_still_knows()
    {
        // The view asks this instead of keeping its own set, so an alias present in
        // the markup but missing from the table would be shown yet unrecognised.
        foreach (string alias in ProviderSetupGuidance.PickerAliases)
        {
            Assert.True(ProviderSetupGuidance.IsKnown(alias), alias + " is in the picker but not known");
        }

        Assert.False(ProviderSetupGuidance.IsKnown("not-a-provider"));
        Assert.False(ProviderSetupGuidance.IsKnown(null));
        Assert.False(ProviderSetupGuidance.IsKnown("   "));
    }

    [Fact]
    public void An_empty_alias_has_no_provider_behind_it()
    {
        // Two different questions. Find answers "which provider is this", and an empty
        // alias is no provider at all, so the app shows nothing rather than naming one
        // the user never picked. IsKnown answers "is this a recognised alias", and a
        // caller uses it to reject input.
        Assert.Null(ProviderSetupGuidance.Find(null));
        Assert.False(ProviderSetupGuidance.IsKnown(null));
    }

    [Fact]
    public void ModelsDev_choices_are_selectable_without_joining_the_fixed_table()
    {
        // The catalog providers arrive over the network, so no fixed table can list
        // them. The dropdown accepts them through the union check.
        Assert.True(ProviderSetupGuidance.IsModelsDevChoice("models.dev:openrouter"));
        Assert.True(ProviderSetupGuidance.IsModelsDevChoice("Models.Dev:OpenRouter"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice("models.dev:"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice("custom"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice(null));

        Assert.True(ProviderSetupGuidance.IsSelectableProvider("custom"));
        Assert.True(ProviderSetupGuidance.IsSelectableProvider("models.dev:openrouter"));
        Assert.False(ProviderSetupGuidance.IsSelectableProvider("not-a-provider"));
        Assert.False(ProviderSetupGuidance.IsSelectableProvider(null));
    }

    [Fact]
    public void The_picker_offers_only_the_custom_row_beside_the_catalog()
    {
        // OpenAI and Anthropic are retired from selection: provider choice is the
        // Models.dev catalog plus the custom endpoint, which exists for a server the
        // catalog cannot list. Their aliases stay in All so a stored choice is still
        // named and still reports a missing key.
        Assert.Equal(["custom"], ProviderSetupGuidance.PickerAliases);

        Assert.True(ProviderSetupGuidance.IsPickerFixed("custom"));
        Assert.False(ProviderSetupGuidance.IsPickerFixed("openai"));
        Assert.False(ProviderSetupGuidance.IsPickerFixed("anthropic"));
        Assert.False(ProviderSetupGuidance.IsPickerFixed(null));
    }

    [Fact]
    public void NormalizeProviderChoice_lowercases_models_dev_ids()
    {
        Assert.Equal("models.dev:openrouter", ProviderSetupGuidance.NormalizeProviderChoice("Models.Dev:OpenRouter"));
        Assert.Equal("custom", ProviderSetupGuidance.NormalizeProviderChoice("custom"));

        // Nothing to resolve yet: an unknown or absent choice stays unset so the
        // catalog can pick the first provider once it has been fetched.
        Assert.Equal(ProviderSetupGuidance.UnsetChoice, ProviderSetupGuidance.NormalizeProviderChoice("not-a-provider"));
        Assert.Equal(ProviderSetupGuidance.UnsetChoice, ProviderSetupGuidance.NormalizeProviderChoice(null));
    }

    [Fact]
    public void An_unset_choice_resolves_to_the_first_catalog_provider_alphabetically()
    {
        (string, string)[] catalog =
        [
            ("zhipuai", "Zhipu AI"),
            ("abacus", "Abacus"),
            ("openrouter", "OpenRouter"),
        ];

        Assert.Equal(
            "models.dev:abacus",
            ProviderSetupGuidance.ResolveUnsetChoice(null, catalog));
        Assert.Equal(
            "models.dev:abacus",
            ProviderSetupGuidance.ResolveUnsetChoice("not-a-provider", catalog));
    }

    [Fact]
    public void A_chosen_provider_survives_resolution_untouched()
    {
        (string, string)[] catalog = [("abacus", "Abacus"), ("openrouter", "OpenRouter")];

        Assert.Equal(
            "models.dev:openrouter",
            ProviderSetupGuidance.ResolveUnsetChoice("models.dev:openrouter", catalog));
        Assert.Equal("custom", ProviderSetupGuidance.ResolveUnsetChoice("custom", catalog));
    }

    [Fact]
    public void An_empty_catalog_never_invents_a_provider()
    {
        // The list is fetched. While it is unavailable there is no provider to select,
        // and guessing one would point the run at a service the user never configured.
        Assert.Equal(ProviderSetupGuidance.UnsetChoice, ProviderSetupGuidance.ResolveUnsetChoice(null, null));
        Assert.Equal(ProviderSetupGuidance.UnsetChoice, ProviderSetupGuidance.ResolveUnsetChoice(null, []));
    }

    [Fact]
    public void ModelsDev_options_sort_alphabetically_and_drop_duplicates()
    {
        var ordered = ProviderSetupGuidance.OrderModelsDevOptions(
        [
            ("tokengo", "TokenGo"),
            ("ambient", "Ambient"),
            ("blank", "  "),
            ("tokengo", "TokenGo Again"),
            ("", "Nameless"),
        ]);

        Assert.Equal(["ambient", "blank", "tokengo"], ordered.Select(entry => entry.Id).ToArray());
        Assert.Equal(["Ambient", "blank", "TokenGo"], ordered.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Provider_filter_matches_name_or_id_case_insensitively()
    {
        Assert.True(ProviderSetupGuidance.MatchesProviderFilter("OpenRouter", "openrouter", null));
        Assert.True(ProviderSetupGuidance.MatchesProviderFilter("OpenRouter", "openrouter", "  "));
        Assert.True(ProviderSetupGuidance.MatchesProviderFilter("Qiniu", "qiniu-ai", "qiniu"));
        Assert.True(ProviderSetupGuidance.MatchesProviderFilter("Qiniu", "qiniu-ai", "QINIU-AI"));
        Assert.True(ProviderSetupGuidance.MatchesProviderFilter("OpenAI (Official API)", "openai", "official"));
        Assert.False(ProviderSetupGuidance.MatchesProviderFilter("Qiniu", "qiniu-ai", "anthropic"));
    }

    [Fact]
    public void SignedIn_options_merge_fixed_and_models_dev_keys_sorted_by_name()
    {
        var ordered = ProviderSetupGuidance.OrderSignedInOptions(
            [
                ("openai", "OpenAI (Official API)", true),
                ("anthropic", "Anthropic Claude (Official API)", false),
                ("custom", "Custom OpenAI Server", true),
            ],
            [
                ("openrouter", "OpenRouter"),
                ("zai", "Z.AI"),
            ],
            ["zai", "OPENROUTER"]);

        Assert.Equal(
            ["custom", "openai", "models.dev:openrouter", "models.dev:zai"],
            ordered.Select(entry => entry.Value).ToArray());
    }

    [Fact]
    public void SignedIn_options_ignore_key_ids_missing_from_the_catalog()
    {
        var ordered = ProviderSetupGuidance.OrderSignedInOptions(
            [("openai", "OpenAI (Official API)", false)],
            [("openrouter", "OpenRouter")],
            ["ghost"]);

        Assert.Empty(ordered);
    }

    [Fact]
    public void SignedIn_options_tolerate_null_inputs()
    {
        var ordered = ProviderSetupGuidance.OrderSignedInOptions(
            [("openai", "OpenAI (Official API)", true)],
            [("openrouter", "OpenRouter")],
            null);

        Assert.Equal(["openai"], ordered.Select(entry => entry.Value).ToArray());
    }

    /// <summary>
    /// The catalog is ~190 rows, so the picker is a virtualized list, not a panel of
    /// hand-built rows.
    /// </summary>
    /// <remarks>
    /// This is the shape that fixed the picker. Building one Button per provider in a
    /// StackPanel threw 0x800F1000 partway through the catalog, so the list silently
    /// ended in the C's while every layer above it returned all 190 providers. A
    /// ListView materializes only the visible rows, and these assertions keep the list
    /// on that path: virtualized, bound by compiled bindings, and sized by the window.
    /// </remarks>
    [Fact]
    public void The_provider_picker_is_a_virtualized_list()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var list = GuiSource.Require(settings, "ProviderListView");

        Assert.Equal("ListView", list.Name.LocalName);
        Assert.Equal("True", GuiSource.AttributeValue(list, "IsItemClickEnabled"));

        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");
        Assert.Contains("ProviderListView.ItemsSource = _providerRows", code, StringComparison.Ordinal);

        // The old shape must not come back: a row panel with one Button per provider is
        // what hit the element ceiling.
        Assert.Null(settings.Descendants()
            .FirstOrDefault(element => GuiSource.AttributeValue(element, "Name") == "ModelsDevOptionsPanel"));
        Assert.DoesNotContain("BuildProviderRowButton", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list must be given a concrete height, not just a maximum.
    /// </summary>
    /// <remarks>
    /// The picker's content sits in a StackPanel, which measures its children with
    /// unbounded height. A ListView measured that way realizes every item and lays out
    /// none of them: the rows existed, had correct bounds, and painted nothing.
    /// </remarks>
    [Fact]
    public void The_provider_list_is_given_a_concrete_height()
    {
        string body = GuiSource.MethodBody(
            GuiSource.Text("Views", "SettingsView.xaml.cs"),
            "OnProviderFlyoutOpening");

        Assert.Contains("ProviderListView.Height", body, StringComparison.Ordinal);
        Assert.Contains("ProviderListView.MaxHeight", body, StringComparison.Ordinal);
        // The window is the bound, not a constant of our own.
        Assert.Contains("XamlRoot", body, StringComparison.Ordinal);
        Assert.Contains("Math.Max", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item template binds with x:Bind, which is compiled.
    /// </summary>
    /// <remarks>
    /// It used to bind with {Binding}. That resolves by reflection at runtime, and it
    /// silently produced blank rows -- correct bounds, no text, no error.
    /// </remarks>
    [Fact]
    public void The_provider_row_template_binds_compiled()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var template = settings.Descendants()
            .First(element => element.Name.LocalName == "DataTemplate");

        // x:DataType is namespaced, so it is read from the namespace-qualified name.
        var dataType = template.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "DataType");
        Assert.NotNull(dataType);
        Assert.Contains("ProviderRow", dataType!.Value, StringComparison.Ordinal);

        string markup = template.ToString();
        Assert.Contains("x:Bind", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding ", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row model is public and top-level in the Models namespace.
    /// </summary>
    /// <remarks>
    /// x:Bind resolves the DataType by name at compile time, so a nested or namespaced
    /// mismatch fails the build; a model that lands in the wrong namespace has to be
    /// caught by the namespace mapping the markup declares.
    /// </remarks>
    [Fact]
    public void The_provider_row_model_is_addressable_from_the_markup()
    {
        string model = GuiSource.Text("Models", "ProviderRow.cs");

        Assert.Contains("namespace RuApp.Models;", model, StringComparison.Ordinal);
        Assert.Contains("public sealed class ProviderRow", model, StringComparison.Ordinal);

        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        string namespaces = string.Join(
            " ",
            settings.Root!.Attributes()
                .Where(attribute => attribute.IsNamespaceDeclaration)
                .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}"));
        Assert.Contains("models=using:RuApp.Models", namespaces, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_dropdown_has_no_letter_strip()
    {
        // Search plus scroll is the whole way through the catalog: the A-Z
        // jump strip competed with search instead of helping it.
        var markup = GuiSource.Text("Views", "SettingsView.xaml");
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        Assert.DoesNotContain("ProviderLetterJumpStrip", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildProviderLetterStrip", code, StringComparison.Ordinal);
        Assert.DoesNotContain("JumpTargets", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_rows_collapse_with_the_filter()
    {
        // The filter flips IsVisible on the rows; the template must collapse
        // them or typing narrows nothing but the header count.
        var markup = GuiSource.Text("Views", "SettingsView.xaml");

        Assert.Contains("Visibility=\"{x:Bind RowVisibility, Mode=OneWay}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsDev_key_box_saves_itself_without_a_button()
    {
        // No save button: a quiet key box probes and stores itself, so the
        // markup must wire the keystroke handler and drop the old button.
        string markup = GuiSource.Text("Views", "SettingsView.xaml");

        Assert.Contains("PasswordChanged=\"OnModelsDevKeyPasswordChanged\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelsDevSaveButton", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsDev_key_probe_sends_the_canonical_form_and_drops_bad_keys()
    {
        // The backend only accepts "models.dev:id": a stripped bare id is
        // rejected as an invalid payload and the key is never stored. A key
        // the provider calls unauthorized must be removed again, so only a
        // proven key survives.
        string body = GuiSource.MethodBody(
            GuiSource.Text("Views", "SettingsView.xaml.cs"),
            "ProbeAndSaveModelsDevKeyAsync");

        Assert.Contains("NormalizeProviderChoice", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_providerChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..]",
            body,
            StringComparison.Ordinal);
        Assert.Contains("catalog_unauthorized", body, StringComparison.Ordinal);
        Assert.Contains("RemoveProviderAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_flyout_resizes_only_through_the_lifecycle_it_owns()
    {
        string code = GuiSource.Text("Views", "SettingsView.xaml.cs");

        // Subscribed on construction and released on dispose: the handler must not
        // outlive the view, or a closed Settings modal keeps resizing a dead visual tree.
        Assert.Contains("providerFlyout.Opening += OnProviderFlyoutOpening", code, StringComparison.Ordinal);
        Assert.Contains("providerFlyout.Opening -= OnProviderFlyoutOpening", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list must not let Fluent paint its own selection on top of the app's.
    /// </summary>
    /// <remarks>
    /// WinUI's default ListViewItem selected state uses the user's Windows accent colour
    /// plus an indicator bar, so a clicked row carried the system accent, the app's own
    /// fill and the check mark at once, one of them foreign to the palette. The row's
    /// selected look is the app's, and only the app's.
    /// </remarks>
    [Fact]
    public void The_list_does_not_draw_a_second_selection_visual()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var list = GuiSource.Require(settings, "ProviderListView");

        Assert.Equal("None", GuiSource.AttributeValue(list, "SelectionMode"));
        // Clicking still commits a provider, so item click stays enabled.
        Assert.Equal("True", GuiSource.AttributeValue(list, "IsItemClickEnabled"));
    }

    /// <summary>
    /// A search with no hits still reports the count and says so.
    /// </summary>
    /// <remarks>
    /// The count is what tells the user the catalog is intact and the query is narrow.
    /// Collapsing the header at zero matches removed the panel's only guidance exactly
    /// when it was needed.
    /// </remarks>
    [Fact]
    public void The_empty_result_state_still_reports_the_count()
    {
        string body = GuiSource.MethodBody(
            GuiSource.Text("Views", "SettingsView.xaml.cs"),
            "ApplyProviderFilter");

        Assert.DoesNotContain("FixedProvidersHeader.Visibility", body, StringComparison.Ordinal);
        Assert.Contains("ProviderNoMatchText.Visibility", body, StringComparison.Ordinal);

        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var empty = GuiSource.Require(settings, "ProviderNoMatchText");
        Assert.Equal("No provider matches", GuiSource.AttributeValue(empty, "Text"));
        Assert.Equal("Collapsed", GuiSource.AttributeValue(empty, "Visibility"));
    }

    /// <summary>
    /// <see cref="GuiSource.AttributeValue"/> matches a local name, which does not
    /// resolve the namespaced <c>x:Key</c> that identifies a style.
    /// </summary>
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;
}
