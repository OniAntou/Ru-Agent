using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>Durable feed replay keeps run slots stable while limiting parallel reads.</summary>
public sealed class SessionOpenReplayTests
{
    [Fact]
    public async Task Replay_preserves_run_order_and_empty_slots_when_a_run_read_fails()
    {
        var runIds = Enumerable.Range(0, 9)
            .Select(index => new ApiService.SessionRunRef($"run-{index}", true, "completed", null, null))
            .ToArray();
        int activeReads = 0;
        int peakReads = 0;

        var replay = await AgentActivityReplayLoader.LoadAsync(
            "session-a",
            (_, _) => Task.FromResult<IReadOnlyList<ApiService.SessionRunRef>?>(runIds),
            async (runId, _) =>
            {
                int active = Interlocked.Increment(ref activeReads);
                UpdatePeak(active);
                try
                {
                    await Task.Delay(15);
                    if (runId == "run-4") throw new InvalidOperationException("damaged activity log");
                    return new ApiService.RunActivityReplay(
                        Array.Empty<AgentActivityModel>(),
                        [new ApiService.RunTranscriptEntry(0, "assistant_text", runId, null)]);
                }
                finally
                {
                    Interlocked.Decrement(ref activeReads);
                }
            },
            CancellationToken.None);

        Assert.Equal(runIds.Select(run => run.RunId), replay.Select(run => run.RunId));
        Assert.Empty(replay[4].Activities);
        Assert.Empty(replay[4].Transcript);
        Assert.Equal("run-5", replay[5].RunId);
        Assert.InRange(peakReads, 2, AgentActivityReplayLoader.MaximumConcurrentReads);

        void UpdatePeak(int active)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref peakReads);
                if (observed >= active) return;
            } while (Interlocked.CompareExchange(ref peakReads, active, observed) != observed);
        }
    }

    [Fact]
    public async Task Replay_skips_run_listing_for_an_empty_session_id()
    {
        bool listed = false;
        var replay = await AgentActivityReplayLoader.LoadAsync(
            " ",
            (_, _) => { listed = true; return Task.FromResult<IReadOnlyList<ApiService.SessionRunRef>?>([]); },
            (_, _) => Task.FromResult<ApiService.RunActivityReplay?>(null),
            CancellationToken.None);

        Assert.Empty(replay);
        Assert.False(listed);
    }
}
