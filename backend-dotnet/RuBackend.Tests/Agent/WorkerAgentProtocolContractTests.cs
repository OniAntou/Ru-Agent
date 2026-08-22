using System.Text.Json.Nodes;
using RuBackend.Agent;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Agent;

public sealed class WorkerAgentProtocolContractTests
{
    [Fact]
    public void Worker_agent_message_names_are_stable()
    {
        Assert.Equal("chat.tool.request", WorkerMessageTypes.ChatToolRequest);
        Assert.Equal("chat.tool.result", WorkerMessageTypes.ChatToolResult);
        Assert.Equal("chat.tool.cancel", WorkerMessageTypes.ChatToolCancel);
        Assert.Contains(WorkerMessageTypes.ChatToolRequest, WorkerMessageTypes.Known);
        Assert.Contains(WorkerMessageTypes.ChatToolResult, WorkerMessageTypes.Known);
        Assert.Contains(WorkerMessageTypes.ChatToolCancel, WorkerMessageTypes.Known);
    }

    [Fact]
    public void Worker_tool_contracts_keep_request_context_and_safe_result_fields()
    {
        var request = new WorkerToolRequest(
            "tool-request-1",
            "session-1",
            "correlation-1",
            "system.volume",
            "1.0.0",
            "native_mutation",
            new JsonObject { ["operation"] = "mute" },
            30_000);
        var result = new WorkerToolResult(
            request.RequestId,
            "succeeded",
            new JsonObject { ["operation"] = "mute" },
            null,
            "Volume changed.",
            12,
            "verified",
            "default_render_multimedia",
            "0123456789abcdef");

        Assert.Equal("session-1", request.SessionId);
        Assert.Equal("correlation-1", request.CorrelationId);
        Assert.Equal("system.volume", request.ToolId);
        Assert.Equal("native_mutation", request.CapabilityClass);
        Assert.Equal(30_000, request.DeadlineMs);
        Assert.Equal("succeeded", result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal(12, result.DurationMs);
        Assert.Equal("verified", result.VerificationState);
        Assert.Equal("default_render_multimedia", result.EndpointRole);
        Assert.Equal("0123456789abcdef", result.EndpointHash);
    }

    [Fact]
    public void Host_grant_and_approval_models_are_explicit()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var grant = new AgentSessionGrant(
            "session-1",
            new HashSet<string>(["workspace.read"]),
            new HashSet<string>(["research.and_note"]),
            expiresAt);
        var approval = new AgentApprovalRequest(
            "request-1",
            grant.SessionId,
            "files.write",
            "1.0.0",
            "Write one bounded file.",
            "workspace.write",
            expiresAt);

        Assert.Contains("workspace.read", grant.Scopes);
        Assert.Contains("research.and_note", grant.SkillIds);
        Assert.Equal(grant.SessionId, approval.SessionId);
        Assert.Equal("Write one bounded file.", approval.SafeAction);
    }
}
