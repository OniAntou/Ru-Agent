using RuAgent.Harness.Core;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentCancellationCoordinator
{
    public async Task CancelAsync(RunId root, string agentId, CancellationToken ct)
    {
        // The transaction prevents late inbox delivery, admission and continuation
        // before cancellation reaches runtime. Never cancel the root or siblings.
        var owned = await agents.CancelAsync(root, agentId, ct).ConfigureAwait(false);
        Exception? failure = null;
        foreach (var runId in owned)
        {
            try
            {
                await cleanup.CancelAndRemoveRunAsync(runId, ct).ConfigureAwait(false);
                await CloseChildAsync(runId, ct, "subagent_canceled").ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            { failure ??= error; }
        }
        if (failure is not null) throw new InvalidOperationException("subagent_cancel_incomplete", failure);
    }
}
