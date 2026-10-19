using RuAgent.Harness.Core;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentScheduler
{
    private async Task CloseInternalSessionBeforeReleaseAsync(RunId runId, CancellationToken ct)
    {
        if (gateways is null || !await agents.IsInternalRunAsync(runId, ct).ConfigureAwait(false)) return;
        var run = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return;
        // Closing just a run lease leaves detached terminal pumps owned by the
        // session. Drain the internal session before freezing output or reusing
        // its durable identity for a later attempt; parent sessions stay open.
        await gateways.CloseSessionAsync(run.SessionId.Value, ct).ConfigureAwait(false);
    }
}
