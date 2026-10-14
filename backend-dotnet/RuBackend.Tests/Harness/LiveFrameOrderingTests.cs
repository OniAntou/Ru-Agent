using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class LiveFrameOrderingTests
{
    [Fact]
    public async Task Activity_does_not_split_earlier_text_or_overtake_the_next_step()
    {
        var hub = new HarnessLiveEventHub();
        var run = new RunId("run_11111111111111111111111111111111");
        await using var lease = hub.Subscribe(run);
        var expected = new[] { "agent.activity", "assistant.delta", "assistant.delta", "agent.activity", "assistant.delta" };
        foreach (var kind in expected) hub.TryPublish(run, kind, "{}");
        var frames = new List<TransientFrame>();
        while (lease.TryReadNext(out var frame)) frames.Add(frame);
        Assert.Equal(expected, frames.Select(frame => frame.Kind));
        Assert.Equal(frames.OrderBy(frame => frame.LocalSequence), frames);
    }

    [Fact]
    public async Task Delta_overflow_keeps_activity_and_orders_surviving_frames()
    {
        var hub = new HarnessLiveEventHub();
        var run = new RunId("run_22222222222222222222222222222222");
        await using var lease = hub.Subscribe(run);
        for (var i = 0; i < 200; i++)
        {
            hub.TryPublish(run, "assistant.delta", "{}");
            if (i == 150) hub.TryPublish(run, "agent.activity", "{}");
        }
        var frames = new List<TransientFrame>();
        while (lease.TryReadNext(out var frame)) frames.Add(frame);
        Assert.Single(frames, frame => frame.Kind == "agent.activity");
        Assert.True(hub.DroppedCount > 0);
        Assert.Equal(0, hub.ActivityDroppedCount);
        Assert.Equal(frames.OrderBy(frame => frame.LocalSequence), frames);
    }
}
