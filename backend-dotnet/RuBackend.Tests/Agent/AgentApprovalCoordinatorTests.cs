using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Agent;

public sealed class AgentApprovalCoordinatorTests
{
    [Fact]
    public void Register_emits_only_safe_approval_metadata()
    {
        var coordinator = new AgentApprovalCoordinator();
        var tool = new WorkerToolRequest(
            "tool-1",
            "session-1",
            "corr-1",
            "terminal.shell",
            "1.0.0",
            "command_execution",
            new JsonObject { ["script"] = "Get-Content secret.txt" },
            10_000);
        var result = new AgentToolResult(
            "tool-1",
            ToolResultStatuses.ApprovalRequired,
            new JsonObject
            {
                ["approval_id"] = "approval-1",
                ["tool_id"] = "terminal.shell",
                ["tool_version"] = "1.0.0",
                ["scope"] = "terminal.execute",
                ["safe_action"] = "Run a terminal action in the workspace.",
                ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            },
            "approval_required",
            "Approval is required.",
            0,
            ToolVerificationStates.Pending,
            null,
            null,
            false);

        var approval = coordinator.Register(tool, result);
        var json = JsonSerializer.Serialize(approval);

        Assert.Equal("agent.approval_required", approval.Type);
        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("terminal.execute", approval.Scope);
        Assert.DoesNotContain("Get-Content", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("script", json, StringComparison.OrdinalIgnoreCase);
    }
}
