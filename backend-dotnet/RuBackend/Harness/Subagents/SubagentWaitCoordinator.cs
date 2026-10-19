using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Publishes terminal outcomes and completes durable waits without occupying a model worker.</summary>
public sealed class SubagentWaitCoordinator(SqliteHarnessEventStore events, SqliteSubagentResultStore results,
    ITimeProvider time, ILogger<SubagentWaitCoordinator>? logger = null, SubagentCodingResultPublisher? coding = null) : IDisposable
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
                try { await events.TryWakeSubagentWaitAsync(rootId, time.UtcNow, ct).ConfigureAwait(false); }
                catch (HarnessStoreException error)
                { logger?.LogWarning("Subagent wait completion failed for {RunId}: {Code}", rootId.Value, error.Code); }
            }
            _afterWaitRoot = waits.Count == 128 ? waits[^1].Value : null;
        }
        finally { _tick.Release(); }
    }

    public void Dispose() => _tick.Dispose();
}
