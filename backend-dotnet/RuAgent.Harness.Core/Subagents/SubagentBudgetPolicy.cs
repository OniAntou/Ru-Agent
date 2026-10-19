using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

public static class SubagentBudgetPolicy
{
    public static AgentBudgetReservation ReserveModelStep(AgentRun run, DateTimeOffset now) =>
        run.ModelStepsInWindow >= SubagentLimits.MaxModelSteps
            ? Reject(run, true, "subagent_model_step_limit", now)
            : AgentBudgetPolicy.ReserveModelStep(run, now);

    public static AgentBudgetReservation ReserveToolCalls(AgentRun run, int count, DateTimeOffset now) =>
        count > SubagentLimits.MaxToolCalls - run.ToolCallsInWindow
            ? Reject(run, true, "subagent_tool_call_limit", now)
            : AgentBudgetPolicy.ReserveToolCalls(run, count, now);

    public static AgentBudgetReservation Reject(AgentRun run, bool terminal, string code, DateTimeOffset now)
    {
        if (run.State != HarnessRunState.Running) throw new InvalidOperationException("invalid_budget_state");
        var transition = AgentRunStateMachine.TryTransition(run.Status, terminal ? HarnessRunTransition.Fail : HarnessRunTransition.ExhaustBudget);
        if (!transition.Accepted) throw new InvalidOperationException(transition.ErrorCode);
        return AgentBudgetReservation.Reject(run.Advance(transition.Next, run.BudgetWindow, run.ModelStepsInWindow,
            run.ToolCallsInWindow, 1, now, terminal ? code : null), code);
    }
}
