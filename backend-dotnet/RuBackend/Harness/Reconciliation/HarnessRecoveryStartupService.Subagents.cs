using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness.Reconciliation;

public sealed partial class HarnessRecoveryStartupService
{
    private readonly Subagents.SubagentRecoveryCoordinator _subagentRecovery;

    private async Task<(IReadOnlyList<RunId> Ordinary, IReadOnlyList<RunId> Children)> PartitionSubagentRunsAsync(
        IReadOnlyList<RunId> runIds, CancellationToken ct)
    {
        var ordinary = new List<RunId>();
        var children = new List<RunId>();
        foreach (var runId in runIds)
        {
            if (await _subagentRecovery.IsInternalRunAsync(runId, ct).ConfigureAwait(false)) children.Add(runId);
            else ordinary.Add(runId);
        }
        return (ordinary, children);
    }

    private async Task<bool> RecoverSubagentsStageAsync(IReadOnlyList<RunId> children, CancellationToken ct)
    {
        var handled = true;
        foreach (var runId in children)
        {
            try
            {
                if (!await _subagentRecovery.RecoverAsync(runId, ct).ConfigureAwait(false)) handled = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                handled = false;
                _logger.LogWarning("Subagent startup recovery postponed for {RunId}: {Code}", runId.Value,
                    error is HarnessStoreException storeError ? storeError.Code : "subagent_recovery_unavailable");
            }
        }
        return handled;
    }
}
