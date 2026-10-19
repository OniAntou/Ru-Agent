using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Retires legacy user-apply queues and reconciles executions admitted before read-only observation.</summary>
public sealed class SubagentUserApplyCoordinator(SqliteSubagentUserApplyStore actions, IHarnessEventStore events)
{
    private readonly string _epoch = Guid.NewGuid().ToString("N");

    public async Task<bool> DriveAsync(AgentRun root, CancellationToken ct)
    {
        var rows = await actions.ListAsync(root.RunId, ct).ConfigureAwait(false);
        if (root.State.IsTerminal())
        {
            foreach (var row in rows.Where(a => a.Status is "queued" or "awaiting_approval"))
                await actions.UpdateAsync(row, "cancelled", _epoch, null, "subagent_root_terminal", ct).ConfigureAwait(false);
            return false;
        }
        if (root.State != HarnessRunState.Running) return false;
        var action = NextAction(rows);
        if (action is null) return false;
        if (action.Status == "executing")
        {
            // Includes lost final status: never infer non-execution or replay after a crash.
            await ReconcileAsync(root, action, ct).ConfigureAwait(false);
            return true;
        }
        await actions.UpdateAsync(action, "cancelled", _epoch, null, "subagent_user_control_retired", ct).ConfigureAwait(false);
        return true;
    }

    private async Task ReconcileAsync(AgentRun root, SubagentUserApply action, CancellationToken ct)
    {
        var current = await events.GetRunAsync(root.RunId, ct).ConfigureAwait(false);
        if (current?.State == HarnessRunState.Running)
        {
            var saved = await events.CommitAsync(SubagentUserApplyCommit.Create(current, action.Proposal, action.ExecutionId,
                true, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (saved.Outcome != HarnessCommitOutcome.Committed) return;
        }
        if (current is null || current.State.IsTerminal() || current.State is HarnessRunState.Running or HarnessRunState.ReconciliationRequired)
            await actions.UpdateAsync(action, "unknown", _epoch, null, "subagent_apply_reconciliation_required", ct).ConfigureAwait(false);
    }

    public async Task CancelPendingAsync(RunId root, CancellationToken ct)
    {
        foreach (var action in await actions.ListAsync(root, ct).ConfigureAwait(false))
        {
            if (action.Status is "queued" or "awaiting_approval")
                await actions.UpdateAsync(action, "cancelled", _epoch, null, "subagent_authority_closed", ct).ConfigureAwait(false);
        }
    }

    // Lost execution must be reconciled before retiring other legacy actions.
    internal static SubagentUserApply? NextAction(IReadOnlyList<SubagentUserApply> rows) =>
        rows.FirstOrDefault(a => a.Status == "executing")
        ?? rows.FirstOrDefault(a => a.Status is "queued" or "awaiting_approval");
}
