namespace RuAgent.Harness.Core;

public static class AgentRunStateMachine
{
    public static TransitionResult<HarnessRunStatus> TryTransition(
        HarnessRunStatus current,
        HarnessRunTransition transition)
    {
        if (current.State.IsTerminal())
        {
            return TransitionResult<HarnessRunStatus>.Reject(current, "run_terminal");
        }

        if (transition is HarnessRunTransition.Cancel)
        {
            return Accept(HarnessRunState.Cancelled);
        }

        if (transition is HarnessRunTransition.Fail)
        {
            return Accept(HarnessRunState.Failed);
        }

        if (transition is HarnessRunTransition.Interrupt)
        {
            return Accept(HarnessRunState.Interrupted);
        }

        HarnessRunStatus? next = (current.State, transition) switch
        {
            (HarnessRunState.Created, HarnessRunTransition.BeginWorkspaceTrust) =>
                Status(HarnessRunState.WaitingWorkspaceTrust),
            (HarnessRunState.Created, HarnessRunTransition.BeginUserHistory) =>
                History(HistoryWaitKind.UserBeforeFirstStep),
            (HarnessRunState.WaitingWorkspaceTrust, HarnessRunTransition.ApproveWorkspaceTrust) =>
                History(HistoryWaitKind.UserBeforeFirstStep),
            (HarnessRunState.WaitingWorkspaceTrust, HarnessRunTransition.DenyWorkspaceTrust) =>
                Status(HarnessRunState.Cancelled),
            (HarnessRunState.WaitingHistory, HarnessRunTransition.AcknowledgeUserHistory)
                when current.HistoryWaitKind == HistoryWaitKind.UserBeforeFirstStep =>
                Status(HarnessRunState.Running),
            (HarnessRunState.Running, HarnessRunTransition.RequestApproval) =>
                Status(HarnessRunState.WaitingApproval),
            (HarnessRunState.WaitingApproval, HarnessRunTransition.ResolveApproval) =>
                Status(HarnessRunState.Running),
            (HarnessRunState.Running, HarnessRunTransition.ExhaustBudget) =>
                Status(HarnessRunState.WaitingUser),
            (HarnessRunState.WaitingUser, HarnessRunTransition.Continue) =>
                Status(HarnessRunState.Running),
            (HarnessRunState.Running, HarnessRunTransition.RequireReconciliation) =>
                Status(HarnessRunState.ReconciliationRequired),
            (HarnessRunState.ReconciliationRequired, HarnessRunTransition.ApplyReconciliation) =>
                Status(HarnessRunState.Running),
            (HarnessRunState.Running, HarnessRunTransition.BeginFinalHistory) =>
                History(HistoryWaitKind.FinalBeforeCompletion),
            (HarnessRunState.WaitingHistory, HarnessRunTransition.AcknowledgeFinalHistory)
                when current.HistoryWaitKind == HistoryWaitKind.FinalBeforeCompletion =>
                Status(HarnessRunState.Completed),
            _ => null,
        };

        if (next is not null)
        {
            return TransitionResult<HarnessRunStatus>.Accept(next.Value);
        }

        if (current.State == HarnessRunState.WaitingHistory
            && transition is HarnessRunTransition.AcknowledgeUserHistory
                or HarnessRunTransition.AcknowledgeFinalHistory)
        {
            return TransitionResult<HarnessRunStatus>.Reject(current, "history_wait_mismatch");
        }

        return TransitionResult<HarnessRunStatus>.Reject(current, "invalid_run_transition");
    }

    private static TransitionResult<HarnessRunStatus> Accept(HarnessRunState state) =>
        TransitionResult<HarnessRunStatus>.Accept(Status(state));

    private static HarnessRunStatus Status(HarnessRunState state) => new(state);

    private static HarnessRunStatus History(HistoryWaitKind kind) =>
        new(HarnessRunState.WaitingHistory, kind);
}
