using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>ADR 0047: the advisor tool is advertised only to a root run that pinned its marker.</summary>
public sealed class AdvisorHostTests
{
    [Fact]
    public void The_tool_is_advertised_only_to_a_pinned_root_run()
    {
        static bool Advertised(AgentToolRuntimeAvailability availability) =>
            AgentToolRuntimeProjection.Select(availability).Any(definition => definition.ToolId == "advisor");

        var root = new AgentToolRuntimeAvailability(true, true);
        Assert.False(Advertised(root));
        Assert.True(Advertised(root with { AdvisorEnabled = true }));
        Assert.False(Advertised(root with { AdvisorEnabled = true, ChildRole = SubagentRole.Coding }));
        Assert.False(Advertised(root with { AdvisorEnabled = true, ChildRole = SubagentRole.Review }));
    }
}
