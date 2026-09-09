using RuAgent.Harness.Core;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RunUsageTrackerTests
{
    [Fact]
    public void Tracker_Accumulates_PerField_And_Snapshots()
    {
        var tracker = new RuBackend.Harness.RunUsageTracker();
        Assert.Equal((null, null, null), SnapshotOf(tracker));

        tracker.Track(new ModelUsage(inputTokens: 1000, outputTokens: 200, totalTokens: 1200, cacheReadInputTokens: null, cacheWriteInputTokens: null));
        tracker.Track(new ModelUsage(inputTokens: 500, outputTokens: null, totalTokens: null, cacheReadInputTokens: null, cacheWriteInputTokens: null));

        var (input, output, total) = SnapshotOf(tracker);
        Assert.Equal(1500, input);
        Assert.Equal(200, output);
        Assert.Equal(1200, total);
    }

    [Fact]
    public void Tracker_Ignores_Null_Usage()
    {
        var tracker = new RuBackend.Harness.RunUsageTracker();
        tracker.Track(null);
        Assert.Equal((null, null, null), SnapshotOf(tracker));
    }

    private static (long?, long?, long?) SnapshotOf(RuBackend.Harness.RunUsageTracker tracker)
    {
        var s = tracker.Snapshot();
        return (s.InputTokens, s.OutputTokens, s.TotalTokens);
    }
}
