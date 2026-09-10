namespace RuAgent.Harness.Core;

public static class AgentBudgetPolicy
{
    // Bounds one budget window. A window is large enough for a real coding task
    // (many exploratory tool calls plus edit/verify cycles) without letting a
    // runaway loop spend without limit: when a window is exhausted the run moves
    // to WaitingUser and only an explicit Continue opens a fresh window.
    public const int ModelStepLimit = 128;
    public const int ToolCallLimit = 256;

    public static AgentBudgetReservation ReserveModelStep(
        AgentRun run,
        DateTimeOffset updatedAt)
    {
        RequireState(run, HarnessRunState.Running);
        if (run.ModelStepsInWindow >= ModelStepLimit)
        {
            return Exhaust(run, updatedAt, "model_step_budget_exhausted");
        }

        return AgentBudgetReservation.Grant(run.Advance(
            run.Status,
            run.BudgetWindow,
            checked(run.ModelStepsInWindow + 1),
            run.ToolCallsInWindow,
            1,
            updatedAt));
    }

    public static AgentBudgetReservation ReserveToolCalls(
        AgentRun run,
        int count,
        DateTimeOffset updatedAt)
    {
        RequireState(run, HarnessRunState.Running);
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count > ToolCallLimit - run.ToolCallsInWindow)
        {
            return Exhaust(run, updatedAt, "tool_call_budget_exhausted");
        }

        return AgentBudgetReservation.Grant(run.Advance(
            run.Status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            checked(run.ToolCallsInWindow + count),
            1,
            updatedAt));
    }

    public static AgentRun EnterApprovalWait(AgentRun run, DateTimeOffset updatedAt)
    {
        RequireState(run, HarnessRunState.Running);
        return AdvanceTransition(run, HarnessRunTransition.RequestApproval, updatedAt);
    }

    public static AgentRun ResumeAfterApproval(AgentRun run, DateTimeOffset updatedAt)
    {
        RequireState(run, HarnessRunState.WaitingApproval);
        return AdvanceTransition(run, HarnessRunTransition.ResolveApproval, updatedAt);
    }

    public static AgentRun Continue(AgentRun run, DateTimeOffset updatedAt)
    {
        RequireState(run, HarnessRunState.WaitingUser);
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Continue);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        return run.Advance(
            transition.Next,
            checked(run.BudgetWindow + 1),
            0,
            0,
            1,
            updatedAt);
    }

    private static AgentBudgetReservation Exhaust(
        AgentRun run,
        DateTimeOffset updatedAt,
        string errorCode)
    {
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ExhaustBudget);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            updatedAt);
        return AgentBudgetReservation.Reject(next, errorCode);
    }

    private static AgentRun AdvanceTransition(
        AgentRun run,
        HarnessRunTransition transition,
        DateTimeOffset updatedAt)
    {
        var result = AgentRunStateMachine.TryTransition(run.Status, transition);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(result.ErrorCode);
        }

        return run.Advance(
            result.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            updatedAt);
    }

    private static void RequireState(AgentRun? run, HarnessRunState expected)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.State != expected)
        {
            throw new InvalidOperationException("invalid_budget_state");
        }
    }
}

public readonly record struct AgentBudgetReservation(
    bool Granted,
    AgentRun NextRun,
    string? ErrorCode)
{
    public static AgentBudgetReservation Grant(AgentRun nextRun) =>
        new(true, nextRun, null);

    public static AgentBudgetReservation Reject(AgentRun nextRun, string errorCode) =>
        new(false, nextRun, errorCode);
}
