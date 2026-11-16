using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SubagentConcurrencyOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("nope")]
    public void Unset_or_invalid_values_leave_admission_unlimited(string? raw)
    {
        var options = SubagentConcurrencyOptions.FromEnvironment(name =>
            name == SubagentConcurrencyOptions.EnvironmentVariableName ? raw : null);

        Assert.Null(options.MaxRunnable);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    [InlineData("64", 64)]
    [InlineData(" 500 ", 500)]
    public void A_positive_value_sets_a_cap_of_any_size(string raw, int expected)
    {
        var options = SubagentConcurrencyOptions.FromEnvironment(name =>
            name == SubagentConcurrencyOptions.EnvironmentVariableName ? raw : null);

        Assert.Equal(expected, options.MaxRunnable);
    }

    [Fact]
    public void The_environment_variable_is_the_documented_name()
    {
        Assert.Equal("RU_MAX_RUNNABLE_SUBAGENTS", SubagentConcurrencyOptions.EnvironmentVariableName);
        Assert.Equal(1, SubagentConcurrencyOptions.Minimum);
        Assert.Null(new SubagentConcurrencyOptions().MaxRunnable);
    }
}
