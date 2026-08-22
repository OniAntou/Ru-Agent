using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionContractTests
{
    [Fact]
    public void Agent_mode_is_session_scoped_and_disables_on_session_change()
    {
        var service = new AgentSessionService();
        service.Enable("session-1");
        Assert.True(service.IsEnabled);
        Assert.Equal("session-1", service.SessionId);
        Assert.Contains("workspace.read", service.Scopes);
        Assert.Contains("terminal.execute", service.Scopes);

        service.Disable();
        Assert.False(service.IsEnabled);
        Assert.Null(service.SessionId);
        Assert.Empty(service.Scopes);
    }
}
