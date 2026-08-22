using RuAgent.Broker.Policy;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ApprovalTokenServiceTests
{
    [Fact]
    public void Token_is_bound_to_identity_and_can_only_be_consumed_once()
    {
        var service = new ApprovalTokenService();
        var token = service.Issue(
            "request-1",
            "session-1",
            "terminal.shell",
            "1.0.0",
            "arguments-hash",
            TimeSpan.FromSeconds(30));

        var expected = new ApprovalBinding(
            "request-1",
            "session-1",
            "terminal.shell",
            "1.0.0",
            "arguments-hash",
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(service.TryConsume(token, expected, out var consumed));
        Assert.NotNull(consumed);
        Assert.False(service.TryConsume(token, expected, out _));
    }

    [Fact]
    public void Token_rejects_a_different_argument_identity()
    {
        var service = new ApprovalTokenService();
        var token = service.Issue(
            "request-1",
            "session-1",
            "terminal.shell",
            "1.0.0",
            "arguments-hash",
            TimeSpan.FromSeconds(30));

        var wrongArguments = new ApprovalBinding(
            "request-1",
            "session-1",
            "terminal.shell",
            "1.0.0",
            "different-hash",
            DateTimeOffset.UtcNow);

        Assert.False(service.TryConsume(token, wrongArguments, out _));
    }
}
