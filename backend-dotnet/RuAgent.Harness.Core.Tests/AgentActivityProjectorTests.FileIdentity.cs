using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentActivityProjectorTests
{
    [Theory]
    [InlineData(AgentActivityState.Started)]
    [InlineData(AgentActivityState.Failed)]
    public void A_write_missing_op_is_never_labeled_as_edit(AgentActivityState state)
    {
        var proposal = Proposal("write", """{"path":"new.html","content":"hello"}""");
        var activity = AgentActivityProjector.ProjectTool(proposal, state, safeCode: "malformed_call");
        Assert.Equal("Write", activity.ToolDisplay);
    }
}
