using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The Host-side approval/token state machine shared by the in-process gateway:
/// lease-scoped ask registration, single-use authorization tokens bound to the
/// full lease identity, and generation invalidation across a rebind. Ported
/// from the deleted wire factory suite; the state class is unchanged and the
/// in-process factory drives it exactly the same way.
/// </summary>
public sealed class BrokerApprovalStateTests
{
    [Theory]
    [InlineData("lease")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("generation")]
    [InlineData("session")]
    [InlineData("root")]
    [InlineData("url")]
    [InlineData("auth")]
    public void Cached_authorization_token_is_bound_to_the_current_run_lease(string changedField)
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var lease = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            BrokerUrl: "ws://127.0.0.1:41001/agent",
            AuthTokenSha256: new string('b', 64));
        state.ActivateLease(lease);
        state.StoreAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            "authorization-token",
            DateTimeOffset.UtcNow.AddMinutes(1));
        var stale = changedField switch
        {
            "lease" => lease with { LeaseId = "lease-b" },
            "version" => lease with { PolicyVersion = 5 },
            "hash" => lease with { PolicySha256 = new string('b', 64) },
            "generation" => lease with { Generation = 2 },
            "session" => lease with { SessionId = "session-b" },
            "url" => lease with { BrokerUrl = "ws://127.0.0.1:41002/agent" },
            "auth" => lease with { AuthTokenSha256 = new string('c', 64) },
            _ => lease with { CanonicalRoot = "C:\\workspace-b" },
        };

        Assert.False(state.TryTakeAuthorizationToken(
            runId,
            stale,
            "read|{\"path\":\"README.md\"}",
            out var staleToken));
        Assert.Null(staleToken);
        Assert.True(state.TryTakeAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            out var token));
        Assert.Equal("authorization-token", token);
        Assert.False(state.TryTakeAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            out _));
    }

    [Fact]
    public void Invalidating_an_old_lease_does_not_remove_a_new_lease_token()
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var oldLease = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            "ws://127.0.0.1:41001/agent",
            new string('b', 64));
        var newLease = oldLease with { LeaseId = "lease-b" };
        const string argsKey = "read|{}";
        state.ActivateLease(oldLease);
        state.StoreAuthorizationToken(
            runId,
            oldLease,
            argsKey,
            "old-token",
            DateTimeOffset.UtcNow.AddMinutes(1));
        state.ActivateLease(newLease);
        state.StoreAuthorizationToken(
            runId,
            newLease,
            argsKey,
            "new-token",
            DateTimeOffset.UtcNow.AddMinutes(1));

        state.InvalidateLease(oldLease);

        Assert.False(state.TryTakeAuthorizationToken(runId, oldLease, argsKey, out _));
        Assert.True(state.TryTakeAuthorizationToken(runId, newLease, argsKey, out var token));
        Assert.Equal("new-token", token);
    }

    [Fact]
    public void Stale_generation_response_cannot_be_registered_after_rebind()
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var first = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            "ws://127.0.0.1:41001/agent",
            new string('b', 64));
        var second = first with { Generation = 1 };
        const string argsKey = "read|{\"path\":\"README.md\"}";
        state.ActivateLease(first);

        Assert.True(state.RegisterAsk(
            runId,
            first,
            "apr_current",
            "broker-current",
            argsKey));
        Assert.True(state.StoreAuthorizationToken(
            runId,
            first,
            argsKey,
            "token-current",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        state.ActivateLease(second);

        Assert.False(state.RegisterAsk(
            runId,
            first,
            "apr_stale",
            "broker-stale",
            argsKey));
        Assert.False(state.StoreAuthorizationToken(
            runId,
            first,
            argsKey,
            "token-stale",
            DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.False(state.TryGetAsk(
            runId,
            first,
            "apr_current",
            out _,
            out _));
        Assert.False(state.TryTakeAuthorizationToken(runId, first, argsKey, out _));
    }
}
