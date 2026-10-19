using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SubagentRunStateTests
{
    [Fact]
    public void Create_or_trust_reply_can_race_with_parent_parking_for_children()
    {
        Assert.True(RunStartCore.IsKnownCreateStatus("waiting_subagents"));
        Assert.True(RunStartCore.IsAcceptedTrustStatus("waiting_subagents"));
        Assert.False(RunStartCore.IsKnownCreateStatus("waiting_subagents_unknown"));
    }
}
