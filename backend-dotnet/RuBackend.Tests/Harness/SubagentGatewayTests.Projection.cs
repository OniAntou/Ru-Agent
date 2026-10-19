using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentGatewayTests
{
    [Theory]
    [InlineData(SubagentRole.Coding)]
    [InlineData(SubagentRole.Review)]
    public void Child_projection_omits_user_questions_and_controls_and_keeps_web_availability(SubagentRole role)
    {
        var tools = AgentToolRuntimeProjection.Select(new(false, true, role)).Select(t => t.ToolId).ToArray();
        Assert.Contains("read", tools);
        Assert.Contains("web_fetch", tools);
        Assert.DoesNotContain("web_search", tools);
        Assert.DoesNotContain("ask_user", tools);
        Assert.DoesNotContain(tools, id => id.StartsWith("agent_", StringComparison.Ordinal));
        foreach (var tool in new[] { "write", "edit", "bash" })
            Assert.Equal(role == SubagentRole.Coding, tools.Contains(tool));
        Assert.Equal(AgentToolDefinitionCatalog.Get().Select(t => t.ToolId),
            AgentToolRuntimeProjection.Select(new(true, true)).Select(t => t.ToolId));
        Assert.False(SubagentRoleTools.Includes(role, "agent_spawn"));
        Assert.False(SubagentRoleTools.Includes(role, "future_unknown_tool"));
    }

    private sealed class CapabilityCapture(IModelStepRunner inner) : IModelStepRunner
    {
        public string[] Tools { get; private set; } = [];
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta, CancellationToken ct)
        {
            Tools = request.PromptContext.Capabilities.Select(c => c.Id).ToArray();
            return inner.RunStepAsync(request, publishDelta, ct);
        }
    }
}
