using RuAgent.Broker.Configuration;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalConcurrencyOptionsTests
{
    [Fact]
    public void Missing_terminal_limit_uses_eight_tasks()
    {
        var options = TerminalConcurrencyOptions.FromEnvironment(_ => null);

        Assert.Equal(8, options.MaxConcurrentTasks);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    [InlineData("64", 64)]
    public void Terminal_limit_accepts_values_inside_configured_range(string value, int expected)
    {
        var options = TerminalConcurrencyOptions.FromEnvironment(name =>
            name == TerminalConcurrencyOptions.EnvironmentVariableName ? value : null);

        Assert.Equal(expected, options.MaxConcurrentTasks);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Invalid_terminal_limit_fails_closed(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            TerminalConcurrencyOptions.FromEnvironment(name =>
                name == TerminalConcurrencyOptions.EnvironmentVariableName ? value : null));

        Assert.Contains(TerminalConcurrencyOptions.EnvironmentVariableName, error.Message, StringComparison.Ordinal);
    }
}
