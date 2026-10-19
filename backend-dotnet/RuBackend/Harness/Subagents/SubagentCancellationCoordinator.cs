using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentCancellationCoordinator(IHarnessEventStore events, SqliteSubagentStore agents,
    IAgentRuntimeRunCleanup cleanup, ITimeProvider time)
{
    public async Task<bool> CancelFamilyAsync(RunId root, CancellationToken ct)
    {
        await agents.CancelFamilyAsync(root, ct).ConfigureAwait(false);
        var owned = await agents.ListOwnedRunIdsAsync(root, ct).ConfigureAwait(false);
        if (owned.Count == 0) return false;
        Exception? failure = null;
        try { await cleanup.CancelAndRemoveRunAsync(root, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) { failure = error; }
        foreach (var childRunId in owned)
        {
            try
            {
                await cleanup.CancelAndRemoveRunAsync(childRunId, ct).ConfigureAwait(false);
                await CloseChildAsync(childRunId, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) { failure ??= error; }
        }
        if (failure is not null) throw new InvalidOperationException("subagent_cancel_incomplete", failure);
        return true;
    }

    public async Task CancelSessionAsync(string sessionId, CancellationToken ct)
    {
        string? after = null;
        while (true)
        {
            var roots = await agents.ListFamiliesForSessionAsync(new(sessionId), after, ct).ConfigureAwait(false);
            foreach (var root in roots) await CancelFamilyAsync(root, ct).ConfigureAwait(false);
            if (roots.Count < SqliteSubagentStore.FamilyPageSize) return;
            after = roots[^1].Value;
        }
    }

    private async Task CloseChildAsync(RunId runId, CancellationToken ct, string code = "subagent_parent_canceled")
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var run = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.State.IsTerminal()) return;
            var commit = SubagentAdmission.CreateTerminalCommit(run, new("evt_" + Guid.NewGuid().ToString("N")),
                true, code, time.UtcNow);
            var result = await events.CommitAsync(commit, ct).ConfigureAwait(false);
            if (result.Outcome == HarnessCommitOutcome.Committed) return;
            if (result.Outcome != HarnessCommitOutcome.VersionConflict) throw new HarnessStoreException("subagent_cancel_incomplete");
        }
        throw new HarnessStoreException("subagent_cancel_conflict");
    }
}
