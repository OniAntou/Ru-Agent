using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentApprovalTests
{
    [Fact]
    public void Approval_expires_and_exposes_only_safe_action()
    {
        var approval = new AgentApprovalModel(
            "request-1",
            "session-1",
            "Run the approved diagnostic.",
            "terminal.execute",
            DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.False(approval.IsExpired());
        Assert.True(approval.IsExpired(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.DoesNotContain("token", approval.SafeAction, StringComparison.OrdinalIgnoreCase);
    }
}
