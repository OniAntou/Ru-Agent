using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class SubagentWaitStateTests
{
    [Fact]
    public void Durable_child_wait_wakes_without_continue_or_new_budget_window()
    {
        var wait = AgentRunStateMachine.TryTransition(new(HarnessRunState.Running), HarnessRunTransition.WaitForSubagents);
        Assert.True(wait.Accepted);
        Assert.Equal(HarnessRunState.WaitingSubagents, wait.Next.State);
        Assert.False(wait.Next.State.IsTerminal());
        Assert.False(AgentRunStateMachine.TryTransition(wait.Next, HarnessRunTransition.Continue).Accepted);
        var ready = AgentRunStateMachine.TryTransition(wait.Next, HarnessRunTransition.SubagentsReady);
        Assert.True(ready.Accepted);
        Assert.Equal(HarnessRunState.Running, ready.Next.State);
    }

    [Theory]
    [InlineData(HarnessRunTransition.Cancel)]
    [InlineData(HarnessRunTransition.Fail)]
    [InlineData(HarnessRunTransition.Interrupt)]
    public void Waiting_root_can_terminate(HarnessRunTransition transition) =>
        Assert.True(AgentRunStateMachine.TryTransition(new(HarnessRunState.WaitingSubagents), transition).Next.State.IsTerminal());
}
