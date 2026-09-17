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
    [InlineData("opencode", "OpenCode Zen")]
    [InlineData("kilo", "Kilo Gateway")]
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
        Assert.Equal("OpenCode Zen", ProviderSetupGuidance.DisplayName(null));
        Assert.Equal("OpenCode Zen", ProviderSetupGuidance.DisplayName("   "));
    }

    [Fact]
    public void Every_provider_except_custom_points_at_where_a_key_comes_from()
    {
        foreach (var provider in ProviderSetupGuidance.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.KeyHint));

            if (provider.Alias == "custom")
            {
                // A custom endpoint is whatever URL the user typed, so there is no
                // single page to send them to.
                Assert.Null(provider.KeyUrl);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(provider.KeyUrl));
            Assert.StartsWith("https://", provider.KeyUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_provider_without_a_key_is_reported_as_missing()
    {
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "openai"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "anthropic"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "kilo"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "opencode"));
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

    [Theory]
    [InlineData("http://localhost:8000/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://[::1]:1234/v1")]
    [InlineData("http://LOCALHOST:5000/v1")]
    public void A_local_endpoint_does_not_need_a_key(string url)
    {
        // Ollama and LM Studio run on this machine and take no key. Warning here
        // would be wrong for a setup that already works.
        var settings = new AppSettingsDto { CustomUrl = url };

        Assert.False(ProviderSetupGuidance.IsMissingKey(settings, "custom"));
        Assert.False(ProviderSetupGuidance.RequiresKey("custom", url));
    }

    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://192.168.1.50:8000/v1")]
    public void A_remote_custom_endpoint_still_needs_a_key(string url)
    {
        var settings = new AppSettingsDto { CustomUrl = url };

        Assert.True(ProviderSetupGuidance.IsMissingKey(settings, "custom"));
        Assert.True(ProviderSetupGuidance.RequiresKey("custom", url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void An_unusable_custom_url_is_treated_as_needing_a_key(string url)
    {
        // Nothing can be assumed about a URL that does not parse, and a remote
        // endpoint is the safer assumption: a missing key surfaces as an error,
        // while a silently keyless request surfaces as a confusing 401.
        Assert.True(ProviderSetupGuidance.RequiresKey("custom", url));
    }

    [Fact]
    public void A_non_custom_provider_needs_a_key_regardless_of_the_custom_url()
    {
        // CustomUrl belongs to the custom provider; it must not suppress the
        // warning for a hosted provider that happens to be selected.
        Assert.True(ProviderSetupGuidance.RequiresKey("openai", "http://localhost:8000/v1"));
        Assert.True(ProviderSetupGuidance.IsMissingKey(Empty(), "openai"));
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
    public void The_openai_provider_exposes_its_key_page_for_a_link()
    {
        var provider = ProviderSetupGuidance.Find("openai");

        Assert.NotNull(provider);
        Assert.Equal("https://platform.openai.com/api-keys", provider.KeyUrl);
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
    public void The_provider_table_covers_every_alias_the_picker_offers()
    {
        // SettingsView.KnownProviders is the picker's list. If a provider is added
        // there and not here, the new provider silently loses its guidance.
        string[] pickerAliases = ["opencode", "kilo", "openai", "anthropic", "custom"];
        var tableAliases = ProviderSetupGuidance.All.Select(p => p.Alias).ToArray();

        Assert.Equal(pickerAliases.OrderBy(a => a, StringComparer.Ordinal), tableAliases.OrderBy(a => a, StringComparer.Ordinal));
    }
}
