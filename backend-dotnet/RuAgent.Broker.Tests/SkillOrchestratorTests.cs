using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillOrchestratorTests
{
    [Fact]
    public async Task Orchestrator_runs_declared_tools_and_returns_safe_summary()
    {
        var tools = new ToolRegistry();
        tools.Register(Tool());
        var invoker = new FakeInvoker();
        var skill = new SkillManifest(
            "research.and_note",
            "1.0.0",
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            new HashSet<string>(["files.list"], StringComparer.Ordinal),
            new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal),
            [new SkillStep(SkillStepKind.CallTool, "list", "files.list", new JsonObject { ["path"] = "src" })],
            false,
            8,
            120_000,
            "builtin",
            "Instructions are data.");

        var result = await new SkillOrchestrator(tools, new ToolPolicyEngine(), invoker).RunAsync(
            "session-1",
            skill,
            new PolicyGrant("session-1", new HashSet<string>([ToolScope.WorkspaceRead]), DateTimeOffset.UtcNow.AddMinutes(1)),
            new JsonObject(),
            CancellationToken.None);

        Assert.Equal(SkillRunState.Completed, result.State);
        Assert.Equal("safe result", result.SafeSummary);
        Assert.Equal("files.list", invoker.ToolId);
    }

    [Fact]
    public async Task Orchestrator_pauses_for_approval_and_rejects_missing_declaration()
    {
        var tools = new ToolRegistry();
        tools.Register(Tool(RiskClass.External));
        var skill = new SkillManifest(
            "external.skill",
            "1.0.0",
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            new HashSet<string>(["files.list"], StringComparer.Ordinal),
            new HashSet<string>([ToolScope.WorkspaceRead]),
            [new SkillStep(SkillStepKind.CallTool, "list", "files.list")],
            false,
            8,
            120_000,
            "builtin",
            "");

        var result = await new SkillOrchestrator(tools, new ToolPolicyEngine(), new FakeInvoker()).RunAsync(
            "session-1",
            skill,
            new PolicyGrant("session-1", new HashSet<string>([ToolScope.WorkspaceRead]), DateTimeOffset.UtcNow.AddMinutes(1)),
            new JsonObject(),
            CancellationToken.None);

        Assert.Equal(SkillRunState.ApprovalRequired, result.State);
    }

    private static ToolManifest Tool(RiskClass risk = RiskClass.Read) => new(
        "files.list",
        "1.0.0",
        "builtin",
        risk,
        new HashSet<string>([ToolScope.WorkspaceRead]),
        new JsonObject { ["type"] = "object" },
        new JsonObject { ["type"] = "object" },
        10_000,
        64 * 1024,
        "builtin");

    private sealed class FakeInvoker : IToolInvoker
    {
        public string? ToolId { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            string sessionId,
            ToolManifest manifest,
            JsonObject arguments,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            ToolId = manifest.Id;
            return Task.FromResult(new ToolResult(
                "tool-1",
                sessionId,
                ToolResultStatuses.Succeeded,
                new JsonObject { ["ok"] = true },
                null,
                "safe result",
                1));
        }
    }
}
