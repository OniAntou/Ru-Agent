using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SubagentConcurrencyOptionsTests
{
    [Theory]
    [InlineData(null, SubagentLimits.MaxRunnable)]
    [InlineData("", SubagentLimits.MaxRunnable)]
    [InlineData("0", SubagentLimits.MaxRunnable)]
    [InlineData("9", SubagentLimits.MaxRunnable)]
    [InlineData("nope", SubagentLimits.MaxRunnable)]
    [InlineData("1", 1)]
    [InlineData("8", SubagentConcurrencyOptions.Maximum)]
    public void Invalid_or_out_of_range_values_fail_closed_to_the_default(string? raw, int expected)
    {
        var options = SubagentConcurrencyOptions.FromEnvironment(name =>
            name == SubagentConcurrencyOptions.EnvironmentVariableName ? raw : null);

        Assert.Equal(expected, options.MaxRunnable);
    }

    [Fact]
    public void The_environment_variable_is_the_documented_name()
    {
        Assert.Equal("RU_MAX_RUNNABLE_SUBAGENTS", SubagentConcurrencyOptions.EnvironmentVariableName);
        Assert.Equal(SubagentLimits.MaxIdentities, SubagentConcurrencyOptions.Maximum);
        Assert.True(SubagentConcurrencyOptions.Minimum >= 1);
    }
}
