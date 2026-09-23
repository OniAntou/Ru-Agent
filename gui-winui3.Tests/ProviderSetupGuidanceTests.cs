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
    public void An_absent_alias_falls_back_to_the_default_provider()
    {
        Assert.Equal("OpenAI", ProviderSetupGuidance.DisplayName(null));
        Assert.Equal("OpenAI", ProviderSetupGuidance.DisplayName("   "));
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
    public void The_provider_table_covers_every_option_the_picker_renders()
    {
        // Read from the markup, which is the picker's real list, rather than from a
        // third copy of the aliases. An earlier version of this test compared the
        // table against a hardcoded array while the view kept its own set beside it,
        // so three lists had to agree and the test checked only two of them.
        var document = GuiSource.Xml("Views", "SettingsView.xaml");

        var pickerAliases = document.Descendants()
            .Where(element => GuiSource.AttributeValue(element, "Click") == "OnProviderOptionClick")
            .Select(element => GuiSource.AttributeValue(element, "Tag") ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(pickerAliases);

        var tableAliases = ProviderSetupGuidance.All
            .Select(provider => provider.Alias)
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pickerAliases, tableAliases);
    }

    [Fact]
    public void Every_picker_option_is_a_provider_the_app_knows()
    {
        // The view asks this instead of keeping its own set, so an alias present in
        // the markup but missing from the table would be shown yet unrecognised.
        foreach (string alias in ProviderSetupGuidance.All.Select(provider => provider.Alias))
        {
            Assert.True(ProviderSetupGuidance.IsKnown(alias), alias + " is in the table but not known");
        }

        Assert.False(ProviderSetupGuidance.IsKnown("not-a-provider"));
        Assert.False(ProviderSetupGuidance.IsKnown(null));
        Assert.False(ProviderSetupGuidance.IsKnown("   "));
    }

    [Fact]
    public void An_empty_alias_is_not_known_even_though_Find_defaults_it()
    {
        // Two different questions. Find answers "which provider should this be", so an
        // empty alias becomes the default and the app still has something to show.
        // IsKnown answers "is this a recognised alias", and a caller uses it to reject
        // input. Collapsing them would make the picker accept an empty choice.
        Assert.NotNull(ProviderSetupGuidance.Find(null));
        Assert.False(ProviderSetupGuidance.IsKnown(null));
    }

    [Fact]
    public void ModelsDev_choices_are_selectable_without_joining_the_fixed_table()
    {
        // The generic providers arrive over the network, so the fixed table can
        // never list them. The dropdown accepts them through the union check.
        Assert.True(ProviderSetupGuidance.IsModelsDevChoice("models.dev:openrouter"));
        Assert.True(ProviderSetupGuidance.IsModelsDevChoice("Models.Dev:OpenRouter"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice("models.dev:"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice("openai"));
        Assert.False(ProviderSetupGuidance.IsModelsDevChoice(null));

        Assert.True(ProviderSetupGuidance.IsSelectableProvider("openai"));
        Assert.True(ProviderSetupGuidance.IsSelectableProvider("models.dev:openrouter"));
        Assert.False(ProviderSetupGuidance.IsSelectableProvider("not-a-provider"));
        Assert.False(ProviderSetupGuidance.IsSelectableProvider(null));
    }

    [Fact]
    public void NormalizeProviderChoice_lowercases_models_dev_ids()
    {
        Assert.Equal("models.dev:openrouter", ProviderSetupGuidance.NormalizeProviderChoice("Models.Dev:OpenRouter"));
        Assert.Equal("openai", ProviderSetupGuidance.NormalizeProviderChoice("openai"));
        Assert.Equal("openai", ProviderSetupGuidance.NormalizeProviderChoice("not-a-provider"));
        Assert.Equal("openai", ProviderSetupGuidance.NormalizeProviderChoice(null));
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
}
