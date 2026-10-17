using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests.Harness;

/// <summary>
/// Preset input acceptance contract across every CLI/RPC/Host entry point:
/// callers normalize locally-typed preset input (trim, case, the historical
/// "full" alias) to a canonical wire value, while wire-facing validation only
/// accepts exact canonical values.
/// </summary>
public sealed class AgentPolicyProfilePresetInputTests
{
    [Theory]
    [InlineData("plan", "plan")]
    [InlineData("standard", "standard")]
    [InlineData("full_access", "full_access")]
    [InlineData("full", "full_access")]
    [InlineData("Full", "full_access")]
    [InlineData("FULL", "full_access")]
    [InlineData(" full ", "full_access")]
    [InlineData(" full_access ", "full_access")]
    [InlineData("PLAN", "plan")]
    [InlineData("Standard", "standard")]
    [InlineData("Full_Access", "full_access")]
    public void TryNormalizePresetInput_accepts_known_inputs_with_historical_alias(
        string raw,
        string expected)
    {
        Assert.True(AgentPolicyProfileCatalog.TryNormalizePresetInput(raw, out var preset));
        Assert.Equal(expected, preset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("bogus")]
    [InlineData("full-access")]
    [InlineData("fullaccess")]
    public void TryNormalizePresetInput_rejects_unknown_inputs(string? raw)
    {
        Assert.False(AgentPolicyProfileCatalog.TryNormalizePresetInput(raw, out var preset));
        Assert.Equal(string.Empty, preset);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("standard")]
    [InlineData("full_access")]
    public void IsKnownPreset_accepts_exact_wire_values(string preset)
    {
        Assert.True(AgentPolicyProfileCatalog.IsKnownPreset(preset));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData(" full_access ")]
    [InlineData("Full_Access")]
    [InlineData("bogus")]
    public void IsKnownPreset_rejects_everything_but_exact_wire_values(string? preset)
    {
        Assert.False(AgentPolicyProfileCatalog.IsKnownPreset(preset));
    }
}
