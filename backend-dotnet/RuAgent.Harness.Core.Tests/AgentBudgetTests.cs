using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentBudgetTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Last_model_step_in_the_window_is_allowed_and_next_attempt_enters_waiting_user()
    {
        var run = Run(modelSteps: AgentBudgetPolicy.ModelStepLimit - 1);

        var lastAllowed = AgentBudgetPolicy.ReserveModelStep(run, Now.AddSeconds(1));
        var exhausted = AgentBudgetPolicy.ReserveModelStep(
            lastAllowed.NextRun,
            Now.AddSeconds(2));

        Assert.True(lastAllowed.Granted);
        Assert.Equal(AgentBudgetPolicy.ModelStepLimit, lastAllowed.NextRun.ModelStepsInWindow);
        Assert.Equal(HarnessRunState.Running, lastAllowed.NextRun.State);
        Assert.False(exhausted.Granted);
        Assert.Equal("model_step_budget_exhausted", exhausted.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingUser, exhausted.NextRun.State);
        Assert.Equal(AgentBudgetPolicy.ModelStepLimit, exhausted.NextRun.ModelStepsInWindow);
    }

    [Fact]
    public void Last_tool_call_in_the_window_is_allowed_and_batch_reservation_is_atomic()
    {
        var limit = AgentBudgetPolicy.ToolCallLimit;
        var run = Run(toolCalls: limit - 1);

        var lastAllowed = AgentBudgetPolicy.ReserveToolCalls(run, 1, Now.AddSeconds(1));
        var exhausted = AgentBudgetPolicy.ReserveToolCalls(
            lastAllowed.NextRun,
            1,
            Now.AddSeconds(2));
        var oversizedBatch = AgentBudgetPolicy.ReserveToolCalls(
            Run(toolCalls: limit - 1),
            2,
            Now.AddSeconds(1));

        Assert.True(lastAllowed.Granted);
        Assert.Equal(limit, lastAllowed.NextRun.ToolCallsInWindow);
        Assert.False(exhausted.Granted);
        Assert.Equal("tool_call_budget_exhausted", exhausted.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingUser, exhausted.NextRun.State);
        Assert.False(oversizedBatch.Granted);
        Assert.Equal(limit - 1, oversizedBatch.NextRun.ToolCallsInWindow);
    }

    [Fact]
    public void A_window_allows_a_long_coding_run_before_asking_the_user_to_continue()
    {
        // Regression guard for coding autonomy: a single window must comfortably fit
        // many exploratory tool calls plus edit/verify cycles, not just a few steps.
        Assert.True(AgentBudgetPolicy.ModelStepLimit >= 128);
        Assert.True(AgentBudgetPolicy.ToolCallLimit >= 256);
    }

    [Fact]
    public void Approval_wait_and_resume_do_not_consume_either_budget()
    {
        var run = Run(modelSteps: 7, toolCalls: 9);

        var waiting = AgentBudgetPolicy.EnterApprovalWait(run, Now.AddSeconds(1));
        var resumed = AgentBudgetPolicy.ResumeAfterApproval(waiting, Now.AddSeconds(2));

        Assert.Equal(HarnessRunState.WaitingApproval, waiting.State);
        Assert.Equal(7, waiting.ModelStepsInWindow);
        Assert.Equal(9, waiting.ToolCallsInWindow);
        Assert.Equal(HarnessRunState.Running, resumed.State);
        Assert.Equal(7, resumed.ModelStepsInWindow);
        Assert.Equal(9, resumed.ToolCallsInWindow);
    }

    [Fact]
    public void Continue_opens_a_fresh_window_on_the_same_run()
    {
        var run = Run(
            status: HarnessRunState.WaitingUser,
            budgetWindow: 2,
            modelSteps: AgentBudgetPolicy.ModelStepLimit,
            toolCalls: AgentBudgetPolicy.ToolCallLimit);

        var continued = AgentBudgetPolicy.Continue(run, Now.AddSeconds(1));

        Assert.Equal(run.RunId, continued.RunId);
        Assert.Equal(3, continued.BudgetWindow);
        Assert.Equal(0, continued.ModelStepsInWindow);
        Assert.Equal(0, continued.ToolCallsInWindow);
        Assert.Equal(HarnessRunState.Running, continued.State);
        Assert.Equal(run.Version + 1, continued.Version);
        Assert.Equal(run.LastSequence + 1, continued.LastSequence);
    }

    [Fact]
    public void Budget_actions_reject_invalid_states_or_counts()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AgentBudgetPolicy.ReserveModelStep(
                Run(status: HarnessRunState.WaitingApproval),
                Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentBudgetPolicy.ReserveToolCalls(Run(), 0, Now));
        Assert.Throws<InvalidOperationException>(() =>
            AgentBudgetPolicy.Continue(Run(), Now));
    }

    [Fact]
    public void Reconciliation_required_runs_are_not_runnable_for_budget_reservations()
    {
        // A run awaiting reconciliation must never reserve budget: only an explicit
        // ApplyReconciliation transition returns it to the runnable Running state.
        Assert.Throws<InvalidOperationException>(() =>
            AgentBudgetPolicy.ReserveModelStep(
                Run(status: HarnessRunState.ReconciliationRequired),
                Now));
        Assert.Throws<InvalidOperationException>(() =>
            AgentBudgetPolicy.ReserveToolCalls(
                Run(status: HarnessRunState.ReconciliationRequired),
                1,
                Now));
    }

    private static AgentRun Run(
        HarnessRunState status = HarnessRunState.Running,
        int budgetWindow = 1,
        int modelSteps = 0,
        int toolCalls = 0) =>
        new(
            new RunId("run_11111111111111111111111111111111"),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            new HarnessRunStatus(status),
            3,
            5,
            budgetWindow,
            modelSteps,
            toolCalls,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Now,
            Now);
}
