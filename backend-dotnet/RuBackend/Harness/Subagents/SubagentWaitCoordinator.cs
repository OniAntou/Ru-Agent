using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Publishes terminal outcomes and completes durable waits without occupying a model worker.</summary>
public sealed class SubagentWaitCoordinator(SqliteHarnessEventStore events, SqliteSubagentResultStore results,
    ITimeProvider time, ILogger<SubagentWaitCoordinator>? logger = null, SubagentCodingResultPublisher? coding = null,
    IAgentActivitySink? activities = null, SubagentWaitProgressReader? progress = null) : IDisposable
{
    private const int MaxCodingCollectionsPerTick = 1;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private string? _afterResultRun;
    private string? _afterWaitRoot;

    public async Task TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidates = await results.ListUnpublishedAsync(_afterResultRun, ct).ConfigureAwait(false);
            if (candidates.Count == 0 && _afterResultRun is not null)
                candidates = await results.ListUnpublishedAsync(null, ct).ConfigureAwait(false);
            var codingAttempts = 0;
            string? afterAttempt = null;
            foreach (var candidate in candidates)
            {
                var needsCollection = candidate.Role == SubagentRole.Coding && candidate.State == HarnessRunState.Completed;
                // Bound filesystem work per tick, and advance past deferred children
                // so one finishing worker cannot starve the next family.
                if (needsCollection && (coding is null || codingAttempts >= MaxCodingCollectionsPerTick)) continue;
                afterAttempt = candidate.RunId.Value;
                try
                {
                    if (needsCollection)
                    {
                        codingAttempts++;
                        await coding!.PublishAsync(candidate, ct).ConfigureAwait(false);
                    }
                    else await results.PublishTerminalAsync(candidate.RootRunId, candidate.AgentId, candidate.RunId, ct).ConfigureAwait(false);
                }
                catch (HarnessStoreException error)
                { logger?.LogWarning("Subagent result publication failed for {RunId}: {Code}", candidate.RunId.Value, error.Code); }
            }
            _afterResultRun = afterAttempt;
            var waits = await events.ListPendingSubagentWaitsAsync(_afterWaitRoot, ct).ConfigureAwait(false);
            if (waits.Count == 0 && _afterWaitRoot is not null)
                waits = await events.ListPendingSubagentWaitsAsync(null, ct).ConfigureAwait(false);
            foreach (var rootId in waits)
            {
                try
                {
                    // The wait completes here, outside the root's tool pipeline, so its row
                    // is settled here too; otherwise it stays live until the run ends.
                    // A wait about to time out carries what each child is doing, read
                    // before the write transaction; results arriving meanwhile win.
                    IReadOnlyList<SubagentWaitProgress>? snapshot = null;
                    var now = time.UtcNow;
                    if (progress is not null
                        && await events.PeekSubagentWaitAsync(rootId, now, ct).ConfigureAwait(false) is { TimesOutNow: true } probe)
                        snapshot = await progress.ReadAsync(probe.Attempts, ct).ConfigureAwait(false);
                    if (await events.TryWakeSubagentWaitAsync(rootId, now, ct, snapshot).ConfigureAwait(false) is { } woken)
                        AgentActivityProjector.PublishSafe(activities, rootId,
                            AgentActivityProjector.ProjectSubagentWaitSettled(woken.ToolCallId, woken.TimedOut));
                }
                catch (HarnessStoreException error)
                { logger?.LogWarning("Subagent wait completion failed for {RunId}: {Code}", rootId.Value, error.Code); }
            }
            _afterWaitRoot = waits.Count == 128 ? waits[^1].Value : null;
        }
        finally { _tick.Release(); }
    }

    public void Dispose() => _tick.Dispose();
}
