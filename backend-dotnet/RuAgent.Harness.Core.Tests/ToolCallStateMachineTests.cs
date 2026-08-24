using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ToolCallStateMachineTests
{
    public static TheoryData<HarnessToolCallState, ToolCallTransition, HarnessToolCallState> LegalTransitions =>
        new()
        {
            { HarnessToolCallState.Proposed, ToolCallTransition.Allow, HarnessToolCallState.Allowed },
            { HarnessToolCallState.Proposed, ToolCallTransition.RequestApproval, HarnessToolCallState.AwaitingApproval },
            { HarnessToolCallState.Proposed, ToolCallTransition.Deny, HarnessToolCallState.Denied },
            { HarnessToolCallState.AwaitingApproval, ToolCallTransition.ConsumeApproval, HarnessToolCallState.Allowed },
            { HarnessToolCallState.AwaitingApproval, ToolCallTransition.Deny, HarnessToolCallState.Denied },
            { HarnessToolCallState.AwaitingApproval, ToolCallTransition.ExpireApproval, HarnessToolCallState.Proposed },
            { HarnessToolCallState.Allowed, ToolCallTransition.Dispatch, HarnessToolCallState.Executing },
            { HarnessToolCallState.Executing, ToolCallTransition.Succeed, HarnessToolCallState.Succeeded },
            { HarnessToolCallState.Executing, ToolCallTransition.Fail, HarnessToolCallState.Failed },
            { HarnessToolCallState.Executing, ToolCallTransition.TimeOut, HarnessToolCallState.TimedOut },
            { HarnessToolCallState.Executing, ToolCallTransition.Cancel, HarnessToolCallState.Cancelled },
            { HarnessToolCallState.Executing, ToolCallTransition.MarkOutcomeUnknown, HarnessToolCallState.OutcomeUnknown },
        };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Legal_transition_returns_the_exact_next_state(
        HarnessToolCallState current,
        ToolCallTransition transition,
        HarnessToolCallState expected)
    {
        var result = ToolCallStateMachine.TryTransition(current, transition);

        Assert.True(result.Accepted);
        Assert.Equal(expected, result.Next);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Awaiting_approval_cannot_dispatch_without_consuming_approval()
    {
        var result = ToolCallStateMachine.TryTransition(
            HarnessToolCallState.AwaitingApproval,
            ToolCallTransition.Dispatch);

        Assert.False(result.Accepted);
        Assert.Equal(HarnessToolCallState.AwaitingApproval, result.Next);
        Assert.Equal("invalid_tool_call_transition", result.ErrorCode);
    }

    [Fact]
    public void Only_typed_expiry_returns_an_approval_wait_to_proposed()
    {
        var expiry = ToolCallStateMachine.TryTransition(
            HarnessToolCallState.AwaitingApproval,
            ToolCallTransition.ExpireApproval);
        var shortcut = ToolCallStateMachine.TryTransition(
            HarnessToolCallState.Allowed,
            ToolCallTransition.ExpireApproval);

        Assert.True(expiry.Accepted);
        Assert.Equal(HarnessToolCallState.Proposed, expiry.Next);
        Assert.False(shortcut.Accepted);
        Assert.Equal("invalid_tool_call_transition", shortcut.ErrorCode);
    }

    [Theory]
    [InlineData(HarnessToolCallState.Denied)]
    [InlineData(HarnessToolCallState.Succeeded)]
    [InlineData(HarnessToolCallState.Failed)]
    [InlineData(HarnessToolCallState.TimedOut)]
    [InlineData(HarnessToolCallState.Cancelled)]
    [InlineData(HarnessToolCallState.OutcomeUnknown)]
    public void Terminal_state_rejects_every_transition(HarnessToolCallState state)
    {
        foreach (var transition in Enum.GetValues<ToolCallTransition>())
        {
            var result = ToolCallStateMachine.TryTransition(state, transition);

            Assert.False(result.Accepted);
            Assert.Equal(state, result.Next);
            Assert.Equal("tool_call_terminal", result.ErrorCode);
        }
    }

    [Fact]
    public void Invalid_nonterminal_shortcuts_are_rejected()
    {
        AssertRejected(HarnessToolCallState.Proposed, ToolCallTransition.Dispatch);
        AssertRejected(HarnessToolCallState.Allowed, ToolCallTransition.Succeed);
        AssertRejected(HarnessToolCallState.AwaitingApproval, ToolCallTransition.Allow);
        AssertRejected(HarnessToolCallState.Executing, ToolCallTransition.Deny);
    }

    private static void AssertRejected(HarnessToolCallState state, ToolCallTransition transition)
    {
        var result = ToolCallStateMachine.TryTransition(state, transition);
        Assert.False(result.Accepted);
        Assert.Equal(state, result.Next);
        Assert.Equal("invalid_tool_call_transition", result.ErrorCode);
    }
}
