using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRuntimeWakeSignalTests
{
    [Fact]
    public async Task A_wake_ends_the_wait_before_the_poll_interval()
    {
        var signal = new AgentRuntimeWakeSignal();
        signal.Wake();

        var started = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Repeated_wakes_collapse_into_one_pass()
    {
        var signal = new AgentRuntimeWakeSignal();
        signal.Wake();
        signal.Wake();
        signal.Wake();

        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(second.Elapsed >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Without_a_wake_the_wait_falls_back_to_the_poll_interval()
    {
        var signal = new AgentRuntimeWakeSignal();
        var started = System.Diagnostics.Stopwatch.StartNew();

        await signal.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Registering_a_run_wakes_the_loop()
    {
        var signal = new AgentRuntimeWakeSignal();
        var registry = new AgentRuntimeRootRegistry(signal);

        registry.Register(new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Path.GetTempPath());

        var started = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_run_change_reaches_only_that_runs_streams()
    {
        var hub = new HarnessLiveEventHub();
        await using var mine = hub.Subscribe(new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        await using var other = hub.Subscribe(new RunId("run_cccccccccccccccccccccccccccccccc"));

        hub.NotifyRunChanged(new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        hub.NotifyRunChanged(new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.True(mine.ChangedReader.TryRead(out _));
        Assert.False(mine.ChangedReader.TryRead(out _));
        Assert.False(other.ChangedReader.TryRead(out _));
        Assert.False(mine.TryReadNext(out _));
    }
}
