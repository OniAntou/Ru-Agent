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
    public void Register_emits_safe_approval_metadata_with_the_bounded_preview()
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
        var result = ApprovalResult(output =>
        {
            output["approval_id"] = "approval-1";
            output["tool_id"] = "terminal.shell";
            output["tool_version"] = "1.0.0";
            output["scope"] = "terminal.execute";
            output["safe_action"] = "Run a terminal action in the workspace.";
            output["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
            output[ApprovalPreviewFields.Field] = "Get-Content secret.txt";
        });

        var approval = coordinator.Register(tool, result);
        var json = JsonSerializer.Serialize(approval);

        Assert.Equal("agent.approval_required", approval.Type);
        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("terminal.execute", approval.Scope);
        Assert.Equal("Get-Content secret.txt", approval.Preview);
        Assert.Contains("\"preview\":\"Get-Content secret.txt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("script", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_drops_oversized_or_non_string_previews()
    {
        var coordinator = new AgentApprovalCoordinator();
        var tool = ToolRequest();

        var oversized = coordinator.Register(
            tool,
            ApprovalResult(output =>
            {
                output[ApprovalPreviewFields.Field] = new string('p', ApprovalPreviewFields.MaxLength + 1);
            }, "approval-1"));
        var nonString = coordinator.Register(
            tool with { RequestId = "tool-2" },
            ApprovalResult(output =>
            {
                output[ApprovalPreviewFields.Field] = 512;
            }, "approval-2"));

        Assert.Equal("agent.approval_required", oversized.Type);
        Assert.Null(oversized.Preview);
        Assert.Equal("agent.approval_required", nonString.Type);
        Assert.Null(nonString.Preview);
    }

    [Fact]
    public void Register_without_a_preview_keeps_the_frame_valid_and_field_absent()
    {
        var coordinator = new AgentApprovalCoordinator();

        var approval = coordinator.Register(ToolRequest(), ApprovalResult(_ => { }));
        var json = JsonSerializer.Serialize(approval);

        Assert.Equal("agent.approval_required", approval.Type);
        Assert.Null(approval.Preview);
        Assert.DoesNotContain(ApprovalPreviewFields.Field, json, StringComparison.Ordinal);
    }

    private static WorkerToolRequest ToolRequest() => new(
        "tool-1",
        "session-1",
        "corr-1",
        "terminal.shell",
        "1.0.0",
        "command_execution",
        new JsonObject { ["script"] = "Get-Date" },
        10_000);

    private static AgentToolResult ApprovalResult(
        Action<JsonObject> configure,
        string approvalId = "approval-1")
    {
        var output = new JsonObject
        {
            ["approval_id"] = approvalId,
            ["tool_id"] = "terminal.shell",
            ["tool_version"] = "1.0.0",
            ["scope"] = "terminal.execute",
            ["safe_action"] = "Run a terminal action in the workspace.",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
        };
        configure(output);
        return new AgentToolResult(
            "tool-1",
            ToolResultStatuses.ApprovalRequired,
            output,
            "approval_required",
            "Approval is required.",
            0,
            ToolVerificationStates.Pending,
            null,
            null,
            false);
    }
}
