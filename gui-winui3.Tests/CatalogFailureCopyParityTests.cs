using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The settings surfaces phrase catalog failures in their own words (the run-level
/// table talks about a run, these talk about a key box), but the codes themselves
/// come from one place. These tests fail when a code is phrased on a settings
/// surface without being cataloged centrally, which is how the desktop client and
/// the terminal drifted apart before.
/// </summary>
public sealed class CatalogFailureCopyParityTests
{
    [Fact]
    public void Every_catalog_code_the_test_button_phrases_is_cataloged_centrally()
    {
        foreach (var code in ProviderConnectionTestPresentation.PhrasedCodes)
        {
            Assert.True(
                AgentFailureCopy.Table.ContainsKey(code),
                $"'{code}' is phrased by the connection test but missing from AgentFailureCopy.");
        }
    }

    [Fact]
    public void Every_catalog_code_the_custom_editor_phrases_is_cataloged_centrally()
    {
        foreach (var code in CustomProviderPolicy.PhrasedCodes)
        {
            Assert.True(
                AgentFailureCopy.Table.ContainsKey(code),
                $"'{code}' is phrased by the custom provider editor but missing from AgentFailureCopy.");
        }
    }

    [Fact]
    public void The_settings_surfaces_phrase_every_catalog_code_the_run_level_table_knows()
    {
        // A catalog code nobody phrases in Settings would surface as the raw token in
        // a key box. The run-level table is the source of truth for the family.
        var catalogCodes = AgentFailureCopy.Table.Keys
            .Where(code => code.StartsWith("catalog_", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(catalogCodes);
        Assert.All(catalogCodes, code => Assert.Contains(code, ProviderConnectionTestPresentation.PhrasedCodes));
    }
}
