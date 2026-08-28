using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionSelectionTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("default", false)]
    [InlineData(null, false)]
    public void IsValid_matches_backend_session_id_contract(string? sessionId, bool expected)
    {
        Assert.Equal(expected, AgentSessionSelection.IsValid(sessionId));
    }
}
