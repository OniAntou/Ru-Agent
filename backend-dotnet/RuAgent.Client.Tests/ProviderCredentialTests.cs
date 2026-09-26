using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>
/// The one table that answers "which provider is this, and where does its key go".
/// </summary>
/// <remarks>
/// The terminal client and the desktop app each had a copy, and they drifted: the
/// desktop stripped the <c>models.dev:</c> prefix before posting, so the backend
/// rejected every catalog key as an invalid payload while the terminal worked. These
/// pin the shape that makes that drift impossible.
/// </remarks>
public sealed class ProviderCredentialTests
{
    [Theory]
    [InlineData("openrouter")]
    [InlineData("OpenRouter")]
    [InlineData("models.dev:openrouter")]
    [InlineData("  models.dev:OpenRouter  ")]
    public void A_catalog_provider_always_resolves_to_the_canonical_form(string spoken)
    {
        var target = ProviderCredential.Resolve(spoken);

        Assert.NotNull(target);
        Assert.True(target!.IsModelsDev);
        // The prefix is what the backend's normalizer requires; a bare id is rejected
        // as an invalid payload, which is the bug this type exists to prevent.
        Assert.Equal("models.dev:openrouter", target.Choice);
        Assert.Equal("openrouter", target.ModelsDevId);
        Assert.Null(target.KeyField);
    }

    [Fact]
    public void The_catalog_payload_keeps_the_prefix_on_the_wire()
    {
        var target = ProviderCredential.Resolve("openrouter")!;

        var payload = ProviderCredential.KeyPayload(target, "sk-test");

        Assert.Equal("models.dev:openrouter", payload["models_dev_provider_id"]);
        Assert.Equal("sk-test", payload["models_dev_api_key"]);
        Assert.Equal(2, payload.Count);
    }

    [Theory]
    [InlineData("openai", "openai_api_key", "OpenAI")]
    [InlineData("OpenAI API", "openai_api_key", "OpenAI")]
    [InlineData("anthropic", "anthropic_api_key", "Anthropic")]
    [InlineData("claude", "anthropic_api_key", "Anthropic")]
    [InlineData("custom", "custom_api_key", "Custom OpenAI")]
    [InlineData("custom openai", "custom_api_key", "Custom OpenAI")]
    public void A_fixed_provider_carries_its_own_key_field(string spoken, string field, string display)
    {
        var target = ProviderCredential.Resolve(spoken);

        Assert.NotNull(target);
        Assert.False(target!.IsModelsDev);
        Assert.Equal(field, target.KeyField);
        Assert.Equal(display, target.DisplayName);
        Assert.Single(ProviderCredential.KeyPayload(target, "sk-test"));
        Assert.Equal("sk-test", ProviderCredential.KeyPayload(target, "sk-test")[field]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("gemini")]
    [InlineData("models.dev:")]
    [InlineData("models.dev:has spaces")]
    [InlineData("models.dev:../../etc/passwd")]
    public void Anything_unrecognised_resolves_to_nothing(string? spoken)
    {
        // Refusing beats guessing: a guessed id would write a key to an account no
        // provider reads, and the id derives credential account names on the host.
        Assert.Null(ProviderCredential.Resolve(spoken));
    }

    [Fact]
    public void A_catalog_id_longer_than_the_account_bound_is_refused()
    {
        Assert.Null(ProviderCredential.Resolve("models.dev:" + new string('a', 65)));
        Assert.NotNull(ProviderCredential.Resolve("models.dev:" + new string('a', 64)));
    }

    [Theory]
    [InlineData("models.dev:openrouter", true)]
    [InlineData("models.dev:", false)]
    [InlineData("openrouter", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_prefixed_non_empty_value_is_a_catalog_choice(string? choice, bool expected) =>
        Assert.Equal(expected, ProviderCredential.IsModelsDevChoice(choice));

    [Fact]
    public void A_canonical_choice_is_lowercased_and_keeps_its_prefix()
    {
        Assert.Equal("models.dev:openrouter", ProviderCredential.CanonicalModelsDevChoice("models.dev:OpenRouter"));
        Assert.Null(ProviderCredential.CanonicalModelsDevChoice("openrouter"));
    }
}
