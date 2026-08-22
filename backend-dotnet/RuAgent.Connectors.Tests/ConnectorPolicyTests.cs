using RuAgent.Connectors;
using Xunit;

namespace RuAgent.Connectors.Tests;

public sealed class ConnectorPolicyTests
{
    [Fact]
    public void Session_expires_and_requires_exact_scope()
    {
        var session = new ConnectorSession(
            "calendar",
            "session-1",
            new HashSet<string>(["calendar.write"]),
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(session.Allows("calendar.write"));
        Assert.False(session.Allows("mail.send"));
        Assert.False(session.Allows("calendar.write", DateTimeOffset.UtcNow.AddMinutes(2)));
    }
}
