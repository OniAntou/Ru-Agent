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

    [Fact]
    public void Token_rejects_mismatched_snapshot_version()
    {
        var service = new ApprovalTokenService();
        var token = service.Issue(
            "request-1",
            "session-1",
            "files.read",
            "1.0.0",
            "hash",
            TimeSpan.FromSeconds(30),
            snapshotVersion: 5,
            intentHash: new string('a', 64));

        var wrongVersion = new ApprovalBinding(
            "request-1",
            "session-1",
            "files.read",
            "1.0.0",
            "hash",
            DateTimeOffset.UtcNow.AddMinutes(5),
            SnapshotVersion: 6,
            IntentHash: new string('a', 64));

        Assert.False(service.TryConsume(token, wrongVersion, out _));
    }

    [Fact]
    public void Token_rejects_mismatched_intent_hash()
    {
        var service = new ApprovalTokenService();
        var token = service.Issue(
            "request-1",
            "session-1",
            "files.read",
            "1.0.0",
            "hash",
            TimeSpan.FromSeconds(30),
            snapshotVersion: 5,
            intentHash: new string('a', 64));

        var wrongHash = new ApprovalBinding(
            "request-1",
            "session-1",
            "files.read",
            "1.0.0",
            "hash",
            DateTimeOffset.UtcNow.AddMinutes(5),
            SnapshotVersion: 5,
            IntentHash: new string('b', 64));

        Assert.False(service.TryConsume(token, wrongHash, out _));
    }

    [Fact]
    public void V1_token_with_defaults_remains_compatible()
    {
        var service = new ApprovalTokenService();
        var token = service.Issue(
            "request-2",
            "session-2",
            "terminal.shell",
            "1.0.0",
            "hash2",
            TimeSpan.FromSeconds(30));

        var expected = new ApprovalBinding(
            "request-2",
            "session-2",
            "terminal.shell",
            "1.0.0",
            "hash2",
            DateTimeOffset.UtcNow.AddMinutes(5),
            SnapshotVersion: 0,
            IntentHash: "");

        Assert.True(service.TryConsume(token, expected, out _));
    }
}
