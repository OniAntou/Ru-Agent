using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed class SubagentFamilyBudget(
    IHarnessEventStore events,
    SqliteSubagentStore agents,
    SqliteSubagentBudgetStore budgets,
    ITimeProvider time) : IAgentFamilyBudget
{
    public Task<bool> ReserveModelAsync(AgentRun run, ModelStepId step, CancellationToken ct) =>
        ReserveAsync(run, step, SubagentBudgetKind.ModelStep, 1, ct);

    public Task<bool> ReserveToolsAsync(AgentRun run, ModelStepId step, int count, CancellationToken ct) =>
        ReserveAsync(run, step, SubagentBudgetKind.ToolCall, count, ct);

    private async Task<bool> ReserveAsync(AgentRun run, ModelStepId step, SubagentBudgetKind kind, int amount, CancellationToken ct)
    {
        if (run.Status.State != HarnessRunState.Running) return false;
        var owner = await agents.FindOwnerAsync(run.SessionId, ct).ConfigureAwait(false);
        if (owner is null && await agents.IsInternalRunAsync(run.RunId, ct).ConfigureAwait(false)) return false;
        var rootId = owner?.RootRunId ?? run.RunId;
        if (owner is null && !await agents.HasFamilyAsync(rootId, ct).ConfigureAwait(false)) return true;
        if (owner is not null)
        {
            var attempt = await agents.GetLatestAttemptAsync(rootId, owner.AgentId, ct).ConfigureAwait(false);
            if (owner.Canceled || attempt is null || attempt.Run.RunId != run.RunId
                || attempt.Run.Status.State != HarnessRunState.Running || attempt.AdmittedAt is null
                || attempt.DeadlineAt is null || time.UtcNow >= attempt.DeadlineAt) return false;
        }
        var root = await events.GetRunAsync(rootId, ct).ConfigureAwait(false);
        if (root is null || root.Status.State.IsTerminal()) return false;
        // Reserve the complete tool batch once, before dispatch. First spawn seeds
        // the pool with the parent's already-reserved counters, including its batch.
        var key = (kind == SubagentBudgetKind.ModelStep ? "model_" : "tools_") + step.Value;
        return await budgets.ReserveAmountAsync(rootId, root.BudgetWindow, key, kind, amount,
            root.ModelStepsInWindow, root.ToolCallsInWindow, ct).ConfigureAwait(false);
    }
}
