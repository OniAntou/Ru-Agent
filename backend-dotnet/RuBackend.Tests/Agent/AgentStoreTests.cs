using RuBackend.Agent;
using Xunit;

namespace RuBackend.Tests.Agent;

public sealed class AgentStoreTests
{
    [Fact]
    public void Grant_store_requires_scope_and_expires_on_read()
    {
        var store = new AgentSessionGrantStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        store.Put(new AgentSessionGrant(
            "session-1",
            new HashSet<string>(["workspace.read"]),
            new HashSet<string>(),
            expiresAt));

        Assert.True(store.TryGet("session-1", "workspace.read", out var grant));
        Assert.Equal("session-1", grant!.SessionId);
        Assert.False(store.TryGet("session-1", "workspace.write", out _));
        Assert.False(store.TryGet("session-1", "workspace.read", out _, expiresAt.AddMilliseconds(1)));
    }

    [Fact]
    public void Grant_store_requires_every_scope_and_exposes_immutable_copies()
    {
        var scopes = new HashSet<string>(["workspace.read", "workspace.write"], StringComparer.Ordinal);
        var store = new AgentSessionGrantStore();
        store.Put(new AgentSessionGrant(
            "session-all",
            scopes,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow.AddMinutes(1)));
        scopes.Clear();

        Assert.True(store.TryGet(
            "session-all",
            new HashSet<string>(["workspace.read", "workspace.write"], StringComparer.Ordinal),
            out var grant));
        Assert.False(store.TryGet(
            "session-all",
            new HashSet<string>(["workspace.read", "terminal.execute"], StringComparer.Ordinal),
            out _));
        Assert.Equal(new[] { "workspace.read", "workspace.write" }, grant!.Scopes.Order(StringComparer.Ordinal));
        Assert.False(grant.Scopes is HashSet<string>);
        Assert.True(store.Remove("session-all"));
        Assert.False(store.TryGet("session-all", "workspace.read", out _));
    }

    [Fact]
    public void Approval_store_rejects_wrong_session_and_consumes_once()
    {
        var store = new AgentApprovalStore();
        store.Put(new AgentApprovalRequest(
            "approval-1",
            "session-1",
            "files.write",
            "1.0.0",
            "Write one file.",
            "workspace.write",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.False(store.TryTake("approval-1", "other-session", out _));
        Assert.True(store.TryTake("approval-1", "session-1", out var consumed));
        Assert.Equal("approval-1", consumed!.ApprovalId);
        Assert.False(store.TryTake("approval-1", "session-1", out _));
    }

    [Fact]
    public void Approval_store_keeps_only_safe_metadata_for_the_public_host()
    {
        var store = new AgentApprovalStore();
        store.Put(new AgentApprovalRequest(
            "approval-safe-1",
            "session-1",
            "terminal.shell",
            "1.0.0",
            "Run a terminal action in the workspace.",
            "terminal.execute",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.True(store.TryTake("approval-safe-1", "session-1", out var request));
        Assert.Equal("Run a terminal action in the workspace.", request!.SafeAction);
        Assert.False(store.TryTake("approval-safe-1", "session-1", out _));
    }
}
