using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuBackend.Harness;

/// <summary>Closes the durable iterator before payload enrichment or network backpressure.</summary>
internal static class HarnessEventReplayBatch
{
    internal const int MaximumEvents = 128;

    internal static async Task<IReadOnlyList<HarnessEventEnvelope>> ReadAsync(
        IHarnessEventStore store, RunId run, long after, long through, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var events = new List<HarnessEventEnvelope>(MaximumEvents);
        if (after >= through) return events;
        await foreach (var envelope in store.ReadEventsAsync(run, after, ct).ConfigureAwait(false))
        {
            if (envelope.Event.Sequence > through) break;
            events.Add(envelope);
            if (events.Count == MaximumEvents) break;
        }
        return events;
    }
}
