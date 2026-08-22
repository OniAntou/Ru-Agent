using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentApprovalTests
{
    [Fact]
    public void Approval_expires_and_exposes_only_safe_action()
    {
        var approval = new AgentApprovalModel(
            "approval-1",
            "session-1",
            "terminal.shell",
            "Run the approved diagnostic.",
            "terminal.execute",
            DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("terminal.shell", approval.ToolId);
        Assert.False(approval.IsExpired());
        Assert.True(approval.IsExpired(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.DoesNotContain("token", approval.SafeAction, StringComparison.OrdinalIgnoreCase);
    }
}
