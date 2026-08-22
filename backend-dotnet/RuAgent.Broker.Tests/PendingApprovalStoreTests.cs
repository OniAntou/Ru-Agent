using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PendingApprovalStoreTests
{
    [Fact]
    public void Approval_is_bound_to_session_and_consumed_once()
    {
        var store = new PendingApprovalStore();
        var request = Request("request-1", "session-1");
        var manifest = new ToolManifest(
            "terminal.shell",
            "1.0.0",
            "terminal",
            RiskClass.Destructive,
            new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            30_000,
            256 * 1024,
            "builtin");
        var arguments = new JsonObject
        {
            ["shell"] = "powershell",
            ["script"] = "Get-Date",
        };

        var approvalId = store.Issue(
            request,
            manifest,
            arguments,
            "Run a terminal action in the workspace.",
            DateTimeOffset.UtcNow.AddMinutes(1),
            "private-terminal-token");

        Assert.False(string.IsNullOrWhiteSpace(approvalId));
        Assert.False(store.TryConsume(
            approvalId,
            "other-session",
            approved: true,
            DateTimeOffset.UtcNow,
            out _));
        Assert.True(store.TryConsume(
            approvalId,
            "session-1",
            approved: true,
            DateTimeOffset.UtcNow,
            out var pending));
        Assert.Equal("Get-Date", pending!.Arguments["script"]!.GetValue<string>());
        Assert.Equal("private-terminal-token", pending.TerminalApprovalToken);
        Assert.False(store.TryConsume(
            approvalId,
            "session-1",
            approved: true,
            DateTimeOffset.UtcNow,
            out _));
    }

    [Fact]
    public void Expired_or_rejected_approval_is_not_available_for_dispatch()
    {
        var store = new PendingApprovalStore();
        var request = Request("request-2", "session-2");
        var manifest = new ToolManifest(
            "notes.create",
            "1.0.0",
            "builtin",
            RiskClass.External,
            new HashSet<string>([ToolScope.WorkspaceWrite], StringComparer.Ordinal),
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            30_000,
            256 * 1024,
            "builtin");

        var rejectedId = store.Issue(
            request,
            manifest,
            new JsonObject { ["path"] = "notes/rejected.md" },
            "Create a note in the workspace.",
            DateTimeOffset.UtcNow.AddMinutes(1),
            null);
        Assert.True(store.TryConsume(
            rejectedId,
            "session-2",
            approved: false,
            DateTimeOffset.UtcNow,
            out var rejected));
        Assert.False(rejected!.Approved);

        var expiredId = store.Issue(
            request with { RequestId = "request-3" },
            manifest,
            new JsonObject { ["path"] = "notes/expired.md" },
            "Create a note in the workspace.",
            DateTimeOffset.UtcNow.AddSeconds(1),
            null);
        Assert.False(store.TryConsume(
            expiredId,
            "session-2",
            approved: true,
            DateTimeOffset.UtcNow.AddSeconds(2),
            out _));
    }

    private static AgentEnvelope Request(string requestId, string sessionId) => new(
        AgentEnvelopeCodec.ProtocolVersion,
        requestId,
        AgentMessageTypes.ToolRequest,
        sessionId,
        "corr-1",
        30_000,
        new JsonObject());
}
