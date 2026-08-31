using System;
using System.Collections.Generic;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentReplyRenderSchedulingTests
{
    [Fact]
    public void Production_scheduler_coalesces_burst_into_one_snapshot()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("sess-1", "run-1");
        var callbacks = new List<Action>();
        var applied = new List<string>();
        var scheduler = new AgentReplyRenderScheduler(accumulator, callback => callbacks.Add(callback), snapshot => applied.Add(snapshot.Text));

        Assert.True(accumulator.TryAppend("run-1", "step-1", 1, "one"));
        scheduler!.Schedule();
        Assert.True(accumulator.TryAppend("run-1", "step-1", 2, "two"));
        scheduler.Schedule();
        Assert.True(accumulator.TryAppend("run-1", "step-1", 3, "three"));
        scheduler.Schedule();

        Assert.Single(callbacks);
        callbacks[0]();

        Assert.Equal(new[] { "onetwothree" }, applied);
        Assert.Null(accumulator.TakeSnapshot());
    }

    [Fact]
    public void Production_scheduler_does_not_let_stale_callback_consume_new_reply()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("sess-1", "run-1");
        var callbacks = new List<Action>();
        var applied = new List<string>();
        var scheduler = new AgentReplyRenderScheduler(accumulator, callback => callbacks.Add(callback), snapshot => applied.Add(snapshot.Text));

        Assert.True(accumulator.TryAppend("run-1", "step-1", 1, "old"));
        scheduler!.Schedule();
        scheduler.Invalidate();
        accumulator.Begin("sess-2", "run-2");
        Assert.True(accumulator.TryAppend("run-2", "step-1", 1, "new"));
        scheduler.Schedule();

        Assert.Single(callbacks);
        callbacks[0]();

        Assert.Empty(applied);
        Assert.Equal(2, callbacks.Count);
        callbacks[1]();
        Assert.Equal(new[] { "new" }, applied);
    }

    [Fact]
    public void Production_scheduler_can_retry_after_dispatch_rejection()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("sess-1", "run-1");
        var attempts = 0;
        var callbacks = new List<Action>();
        var scheduler = new AgentReplyRenderScheduler(
            accumulator,
            callback =>
            {
                if (++attempts == 1) throw new InvalidOperationException("dispatch rejected");
                callbacks.Add(callback);
            },
            _ => { });

        Assert.True(accumulator.TryAppend("run-1", "step-1", 1, "retry"));
        Assert.Throws<InvalidOperationException>(() => scheduler.Schedule());
        scheduler.Schedule();
        Assert.Single(callbacks);
    }

    [Fact]
    public void Production_scheduler_releases_latch_after_safe_dispatch_cancellation()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("sess-1", "run-1");
        var callbacks = new List<Action>();
        var applied = new List<string>();
        var firstDispatch = true;
        AgentReplyRenderScheduler? scheduler = null;
        scheduler = new AgentReplyRenderScheduler(
            accumulator,
            callback =>
            {
                if (firstDispatch)
                {
                    firstDispatch = false;
                    scheduler!.Invalidate();
                    accumulator.Begin("sess-2", "run-2");
                    Assert.True(accumulator.TryAppend("run-2", "step-1", 1, "new"));
                    scheduler.Schedule();
                    throw new OperationCanceledException();
                }
                callbacks.Add(callback);
            },
            snapshot => applied.Add(snapshot.Text));

        Assert.True(accumulator.TryAppend("run-1", "step-1", 1, "old"));
        scheduler.Schedule();

        Assert.Single(callbacks);
        callbacks[0]();
        Assert.Equal(new[] { "new" }, applied);
    }

    [Fact]
    public void Production_scheduler_flushes_current_text_and_discards_late_callback()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("sess-1", "run-1");
        var callbacks = new List<Action>();
        var applied = new List<string>();
        var scheduler = new AgentReplyRenderScheduler(accumulator, callback => callbacks.Add(callback), snapshot => applied.Add(snapshot.Text));

        Assert.True(accumulator.TryAppend("run-1", "step-1", 1, "final"));
        scheduler.Schedule();

        var flushed = scheduler.Flush();
        Assert.NotNull(flushed);
        Assert.Equal("final", flushed!.Text);
        Assert.Empty(applied);

        callbacks[0]();
        Assert.Empty(applied);
    }
}
