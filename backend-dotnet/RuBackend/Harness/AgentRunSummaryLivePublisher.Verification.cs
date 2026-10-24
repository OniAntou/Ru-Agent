using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuBackend.Harness;

public sealed partial class AgentRunSummaryLivePublisher
{
    public async Task<bool> TryReserveSummaryAsync(AgentRun run, CancellationToken cancellationToken)
    {
        await PrepareVerificationAsync(run, cancellationToken).ConfigureAwait(false);
        return TryReserveSummary(run, out _);
    }

    public async Task<bool> TryPublishSummaryAsync(AgentRun run, CancellationToken cancellationToken)
    {
        await PrepareVerificationAsync(run, cancellationToken).ConfigureAwait(false);
        return TryPublishSummary(run);
    }

    private async Task PrepareVerificationAsync(AgentRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.State.IsTerminal() || _contextSource is null || TryGetTerminalSummary(run.RunId.Value, out _)) return;
        try
        {
            var context = await _contextSource.LoadAsync(run.RunId, ct).ConfigureAwait(false);
            var state = GetOrCreateState(run.RunId.Value);
            if (!ReferenceEquals(state, _discardedOverflowState))
                state.SetVerification(context.VerificationEvidence);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Presentation is best-effort. Missing/deleted history cannot certify
            // a check; keep mutation activity conservatively unverified.
            _logger?.LogWarning("Terminal verification evidence unavailable ({ErrorType})", error.GetType().Name);
        }
    }
}
