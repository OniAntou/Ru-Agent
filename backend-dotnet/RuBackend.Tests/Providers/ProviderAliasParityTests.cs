using RuAgent.Client;
using RuAgent.Protocol.Providers;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// The Host and both clients must agree about what a provider is called. Three
/// copies of that mapping used to drift; these tests walk the one table and check
/// every surface reads the same answer from it.
/// </summary>
public sealed class ProviderAliasParityTests
{
    [Fact]
    public void Every_spoken_alias_means_the_same_provider_on_every_surface()
    {
        Assert.NotEmpty(ProviderAliases.Entries);

        foreach (var entry in ProviderAliases.Entries)
        {
            foreach (var spoken in entry.Spoken)
            {
                Assert.Equal(entry.Backend, ProviderAliases.Normalize(spoken));

                var target = ProviderCredential.Resolve(spoken);
                Assert.NotNull(target);
                Assert.Equal(entry.ClientAlias, target!.Choice);
                Assert.Equal(entry.KeyField, target.KeyField);
                Assert.Equal(entry.IsModelsDev, target.IsModelsDev);

                // The Host derives the credential account from the same alias; a key
                // routed under a different name is a key nothing reads.
                var expectedAccount = entry.IsModelsDev ? entry.ModelsDevId + "_api_key" : entry.KeyField;
                Assert.Equal(expectedAccount, ProviderSelection.CredentialAccountForAlias(spoken));
            }
        }
    }

    [Fact]
    public void The_table_has_no_name_claimed_by_two_providers()
    {
        var owners = new Dictionary<string, ProviderAliasEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ProviderAliases.Entries)
        {
            // One entry may repeat a name (its spoken form can equal its alias); two
            // entries claiming the same name is the drift this table exists to prevent.
            foreach (var name in entry.Spoken.Append(entry.Backend).Append(entry.ClientAlias))
            {
                var key = name.Trim();
                if (owners.TryGetValue(key, out var existing))
                {
                    Assert.Same(entry, existing);
                }
                else
                {
                    owners[key] = entry;
                }
            }
        }
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("kilo")]
    [InlineData("openrouter")]
    [InlineData("claude")]
    [InlineData("gguf")]
    public void The_terminal_can_store_a_key_by_the_bare_name_the_host_accepts(string spoken)
    {
        // These names were accepted by the Host but rejected by /key before the
        // table was shared, so the terminal told users the provider did not exist.
        var target = ProviderCredential.Resolve(spoken);
        Assert.NotNull(target);
        Assert.NotNull(ProviderSelection.CredentialAccountForAlias(spoken));
    }

    [Fact]
    public void A_catalog_key_keeps_the_prefix_the_host_requires()
    {
        var target = ProviderCredential.Resolve("opencode")!;

        var payload = ProviderCredential.KeyPayload(target, "synthetic");

        Assert.Equal("models.dev:opencode", payload["models_dev_provider_id"]);
    }

    [Theory]
    [InlineData("models.dev:OpenRouter", "models.dev:openrouter")]
    [InlineData("  models.dev:kilo  ", "models.dev:kilo")]
    [InlineData("models.dev:", null)]
    [InlineData("models.dev:bad id", null)]
    [InlineData("openrouter", null)]
    public void Models_dev_forms_are_canonical_or_rejected(string input, string? expected)
    {
        Assert.Equal(expected, ProviderAliases.NormalizeModelsDev(input));
    }
}
