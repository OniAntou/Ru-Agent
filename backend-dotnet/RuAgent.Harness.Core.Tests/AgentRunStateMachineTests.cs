using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentRunStateMachineTests
{
    public static TheoryData<HarnessRunStatus, HarnessRunTransition, HarnessRunStatus> LegalTransitions =>
        new()
        {
            { Status(HarnessRunState.Created), HarnessRunTransition.BeginWorkspaceTrust, Status(HarnessRunState.WaitingWorkspaceTrust) },
            { Status(HarnessRunState.Created), HarnessRunTransition.BeginUserHistory, History(HistoryWaitKind.UserBeforeFirstStep) },
            { Status(HarnessRunState.WaitingWorkspaceTrust), HarnessRunTransition.ApproveWorkspaceTrust, History(HistoryWaitKind.UserBeforeFirstStep) },
            { Status(HarnessRunState.WaitingWorkspaceTrust), HarnessRunTransition.DenyWorkspaceTrust, Status(HarnessRunState.Cancelled) },
            { History(HistoryWaitKind.UserBeforeFirstStep), HarnessRunTransition.AcknowledgeUserHistory, Status(HarnessRunState.Running) },
            { Status(HarnessRunState.Running), HarnessRunTransition.RequestApproval, Status(HarnessRunState.WaitingApproval) },
            { Status(HarnessRunState.WaitingApproval), HarnessRunTransition.ResolveApproval, Status(HarnessRunState.Running) },
            { Status(HarnessRunState.Running), HarnessRunTransition.RequestInput, Status(HarnessRunState.WaitingUser) },
            { Status(HarnessRunState.WaitingUser), HarnessRunTransition.ResolveInput, Status(HarnessRunState.Running) },
            { Status(HarnessRunState.Running), HarnessRunTransition.ExhaustBudget, Status(HarnessRunState.WaitingUser) },
            { Status(HarnessRunState.WaitingUser), HarnessRunTransition.Continue, Status(HarnessRunState.Running) },
            { Status(HarnessRunState.Running), HarnessRunTransition.RequireReconciliation, Status(HarnessRunState.ReconciliationRequired) },
            { Status(HarnessRunState.ReconciliationRequired), HarnessRunTransition.ApplyReconciliation, Status(HarnessRunState.Running) },
            { Status(HarnessRunState.Running), HarnessRunTransition.BeginFinalHistory, History(HistoryWaitKind.FinalBeforeCompletion) },
            { History(HistoryWaitKind.FinalBeforeCompletion), HarnessRunTransition.AcknowledgeFinalHistory, Status(HarnessRunState.Completed) },
        };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Legal_transition_returns_the_exact_next_status(
        HarnessRunStatus current,
        HarnessRunTransition transition,
        HarnessRunStatus expected)
    {
        var result = AgentRunStateMachine.TryTransition(current, transition);

        Assert.True(result.Accepted);
        Assert.Equal(expected, result.Next);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void History_acknowledgement_must_match_the_persisted_wait_kind()
    {
        var userMismatch = AgentRunStateMachine.TryTransition(
            History(HistoryWaitKind.UserBeforeFirstStep),
            HarnessRunTransition.AcknowledgeFinalHistory);
        var finalMismatch = AgentRunStateMachine.TryTransition(
            History(HistoryWaitKind.FinalBeforeCompletion),
            HarnessRunTransition.AcknowledgeUserHistory);

        Assert.False(userMismatch.Accepted);
        Assert.Equal("history_wait_mismatch", userMismatch.ErrorCode);
        Assert.False(finalMismatch.Accepted);
        Assert.Equal("history_wait_mismatch", finalMismatch.ErrorCode);
    }

    [Theory]
    [InlineData(HarnessRunState.Completed)]
    [InlineData(HarnessRunState.Failed)]
    [InlineData(HarnessRunState.Cancelled)]
    [InlineData(HarnessRunState.Interrupted)]
    public void Terminal_state_rejects_every_transition(HarnessRunState state)
    {
        var result = AgentRunStateMachine.TryTransition(
            Status(state),
            HarnessRunTransition.Continue);

        Assert.False(result.Accepted);
        Assert.Equal("run_terminal", result.ErrorCode);
        Assert.Equal(Status(state), result.Next);
    }

    [Fact]
    public void Invalid_nonterminal_shortcuts_are_rejected()
    {
        AssertRejected(HarnessRunState.WaitingApproval, HarnessRunTransition.BeginFinalHistory);
        AssertRejected(HarnessRunState.WaitingUser, HarnessRunTransition.BeginFinalHistory);
        AssertRejected(HarnessRunState.ReconciliationRequired, HarnessRunTransition.ResolveApproval);
        AssertRejected(HarnessRunState.Created, HarnessRunTransition.AcknowledgeUserHistory);
    }

    [Fact]
    public void Any_nonterminal_state_can_be_cancelled()
    {
        foreach (var state in Enum.GetValues<HarnessRunState>().Where(state => !state.IsTerminal()))
        {
            var current = state == HarnessRunState.WaitingHistory
                ? History(HistoryWaitKind.UserBeforeFirstStep)
                : Status(state);

            var result = AgentRunStateMachine.TryTransition(current, HarnessRunTransition.Cancel);

            Assert.True(result.Accepted);
            Assert.Equal(Status(HarnessRunState.Cancelled), result.Next);
        }
    }

    [Fact]
    public void Waiting_history_requires_a_kind_and_other_states_forbid_one()
    {
        Assert.Throws<ArgumentException>(() => new HarnessRunStatus(HarnessRunState.WaitingHistory));
        Assert.Throws<ArgumentException>(() =>
            new HarnessRunStatus(HarnessRunState.Running, HistoryWaitKind.UserBeforeFirstStep));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HarnessRunStatus((HarnessRunState)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                (HistoryWaitKind)int.MaxValue));
    }

    private static HarnessRunStatus Status(HarnessRunState state) => new(state);

    private static HarnessRunStatus History(HistoryWaitKind kind) =>
        new(HarnessRunState.WaitingHistory, kind);

    private static void AssertRejected(HarnessRunState state, HarnessRunTransition transition)
    {
        var result = AgentRunStateMachine.TryTransition(Status(state), transition);
        Assert.False(result.Accepted);
        Assert.Equal("invalid_run_transition", result.ErrorCode);
        Assert.Equal(Status(state), result.Next);
    }
}
