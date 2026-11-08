namespace RuAgent.Harness.Core.Subagents;

public static class SubagentBudgetPolicy
{
    // A child used to stop at its own step/tool ceiling, which parked it where nobody
    // could continue it. It now opens windows like a root, so only the family pool and
    // a real failure bound how long a child works.
    public static AgentBudgetReservation ReserveModelStep(AgentRun run, DateTimeOffset now) =>
        AgentBudgetPolicy.ReserveModelStep(run, now, allowAutomaticWindow: true);

    public static AgentBudgetReservation ReserveToolCalls(AgentRun run, int count, DateTimeOffset now) =>
        AgentBudgetPolicy.ReserveToolCalls(run, count, now, allowAutomaticWindow: true);

    public static AgentBudgetReservation Reject(AgentRun run, bool terminal, string code, DateTimeOffset now)
    {
        if (run.State != HarnessRunState.Running) throw new InvalidOperationException("invalid_budget_state");
        var transition = AgentRunStateMachine.TryTransition(run.Status, terminal ? HarnessRunTransition.Fail : HarnessRunTransition.ExhaustBudget);
        if (!transition.Accepted) throw new InvalidOperationException(transition.ErrorCode);
        return AgentBudgetReservation.Reject(run.Advance(transition.Next, run.BudgetWindow, run.ModelStepsInWindow,
            run.ToolCallsInWindow, 1, now, terminal ? code : null), code);
    }
}
