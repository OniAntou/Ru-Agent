using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentUserApplyOrderingTests
{
    [Fact]
    public void Lost_execution_precedes_every_queued_or_waiting_mutation_regardless_of_child_sort_order()
    {
        var root = new RunId("run_" + new string('a', 32));
        var queued = new SubagentUserApply(root, "agent_a", 1, new string('a', 32), "queued", null, null, null);
        var waiting = queued with { AgentId = "agent_b", Status = "awaiting_approval" };
        var lost = queued with { AgentId = "agent_z", Status = "executing" };
        Assert.Same(lost, SubagentUserApplyCoordinator.NextAction([queued, waiting, lost]));
        Assert.Same(lost, SubagentUserApplyCoordinator.NextAction([waiting, lost, queued]));
        Assert.Same(queued, SubagentUserApplyCoordinator.NextAction([queued, lost with { Status = "unknown" }]));
    }
}
