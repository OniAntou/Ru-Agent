using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ReasoningEffortOverrideTests
{
    [Theory]
    [InlineData("low", "low")]
    [InlineData("  high  ", "high")]
    public void Valid_value_is_returned_trimmed(string raw, string expected) =>
        Assert.Equal(expected, ProviderReasoningPreferences.EffortOverride(_ => raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nvalue")]
    public void Missing_or_invalid_value_is_ignored(string? raw) =>
        Assert.Null(ProviderReasoningPreferences.EffortOverride(_ => raw));

    [Fact]
    public void Overlong_value_is_ignored() =>
        Assert.Null(ProviderReasoningPreferences.EffortOverride(_ => new string('a', 33)));

    [Fact]
    public void Reads_the_documented_variable_name()
    {
        string? requested = null;
        ProviderReasoningPreferences.EffortOverride(name => { requested = name; return null; });

        Assert.Equal("RU_REASONING_EFFORT", requested);
    }
}
