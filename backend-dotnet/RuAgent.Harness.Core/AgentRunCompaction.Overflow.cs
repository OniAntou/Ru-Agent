using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunCompaction
{
    internal Task<ContextCompactionResult> RecoverProviderOverflowAsync(RunId runId, CancellationToken ct) =>
        UnderRunGateAsync(runId.Value, async () =>
        {
            var run = await RequireRunAsync(runId, ct).ConfigureAwait(false);
            if (run.State != HarnessRunState.Running) return ContextCompactionResult.NoOp(run, ContextCompactionStrategy.Aggressive);
            var limit = _contextLimit is null ? null : await _contextLimit.MaxPromptTokensAsync(runId, ct).ConfigureAwait(false);
            if (limit is null) return ContextCompactionResult.NotAvailable(run, ContextCompactionStrategy.Aggressive);
            var context = await _contextAssembler.AssembleAsync(runId, ct).ConfigureAwait(false);
            return await CompactSnapshotLockedAsync(run, context, limit.Value, ContextCompactionStrategy.Aggressive,
                ContextCompactionReason.Manual, ct, outputReserveAlreadyAccounted: true,
                forcedTargetTokens: Math.Max(1, ModelContextSizeEstimator.EstimateTokens(context) / 2)).ConfigureAwait(false);
        }, ct);
}
