using System.Text.Json.Nodes;
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
    public void Approval_store_binds_arguments_and_consumes_once()
    {
        var arguments = new JsonObject
        {
            ["path"] = "src",
            ["options"] = new JsonObject { ["recursive"] = true },
        };
        var hash = AgentApprovalStore.ComputeArgumentsHash("files.write", "1.0.0", arguments);
        var store = new AgentApprovalStore();
        store.Put(new AgentApprovalRequest(
            "approval-1",
            "session-1",
            "files.write",
            "1.0.0",
            hash,
            "Write one file.",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.False(store.TryConsume(
            "approval-1",
            "session-1",
            "files.write",
            "1.0.0",
            new JsonObject { ["path"] = "other" },
            out _));
        Assert.True(store.TryConsume(
            "approval-1",
            "session-1",
            "files.write",
            "1.0.0",
            arguments,
            out var consumed));
        Assert.Equal("approval-1", consumed!.RequestId);
        Assert.False(store.TryConsume(
            "approval-1",
            "session-1",
            "files.write",
            "1.0.0",
            arguments,
            out _));
    }
}
