using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>Loads durable run feeds in transcript order with bounded parallel reads.</summary>
internal static class AgentActivityReplayLoader
{
    internal const int MaximumConcurrentReads = 4;

    internal static async Task<IReadOnlyList<SessionRunReplay>> LoadAsync(
        string sessionId,
        Func<string, CancellationToken, Task<IReadOnlyList<ApiService.SessionRunRef>?>> listRuns,
        Func<string, CancellationToken, Task<ApiService.RunActivityReplay?>> listActivities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listRuns);
        ArgumentNullException.ThrowIfNull(listActivities);
        var empty = Array.AsReadOnly(Array.Empty<SessionRunReplay>());
        if (string.IsNullOrWhiteSpace(sessionId)) return empty;

        var runs = await listRuns(sessionId, cancellationToken).ConfigureAwait(false);
        if (runs is null || runs.Count == 0 || cancellationToken.IsCancellationRequested) return empty;

        var slots = new SessionRunReplay?[runs.Count];
        using var gate = new SemaphoreSlim(MaximumConcurrentReads);
        var tasks = new Task[runs.Count];
        for (int index = 0; index < runs.Count; index++)
        {
            int slot = index;
            tasks[slot] = LoadSlotAsync(slot, runs[slot].RunId);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return empty;
        }

        return slots.Select((slot, index) => slot ?? EmptyReplay(runs[index].RunId)).ToArray();

        async Task LoadSlotAsync(int slot, string runId)
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var replay = await listActivities(runId, cancellationToken).ConfigureAwait(false);
                    slots[slot] = replay is null
                        ? EmptyReplay(runId)
                        : new SessionRunReplay(
                            runId,
                            replay.Activities.Select(static model => new AgentActivityItem(model)).ToArray(),
                            replay.Transcript);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                slots[slot] = EmptyReplay(runId);
            }
            catch (Exception)
            {
                // A damaged activity read preserves the transcript slot and lets
                // other runs render instead of shifting later assistant turns.
                slots[slot] = EmptyReplay(runId);
            }
        }
    }

    private static SessionRunReplay EmptyReplay(string runId) =>
        new(runId, Array.Empty<AgentActivityItem>(), Array.Empty<ApiService.RunTranscriptEntry>());
}
