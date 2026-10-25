using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UpdateAvailabilityMonitorTests
{
    [Fact]
    public async Task Starts_with_a_check_then_waits_thirty_minutes_between_checks_and_stops()
    {
        var delay = new ControlledDelay();
        var checks = 0;
        var secondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new UpdateAvailabilityMonitor(_ =>
        {
            if (Interlocked.Increment(ref checks) == 2) secondCheck.TrySetResult();
            return Task.CompletedTask;
        }, _ => { }, delay.DelayAsync);

        var completion = monitor.Start();
        await delay.WaitForCallAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, checks);

        await delay.WaitForCallAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(1, checks);
        delay.Release(TimeSpan.FromMinutes(30));
        await secondCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, checks);

        monitor.Stop();
        await completion;
    }

    [Fact]
    public async Task Request_timeout_cancels_the_check_and_the_monitor_keeps_its_schedule()
    {
        var delay = new ControlledDelay();
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new UpdateAvailabilityMonitor(async token =>
        {
            checkStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { checkCancelled.TrySetResult(); }
        }, _ => { }, delay.DelayAsync);

        var completion = monitor.Start();
        await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.WaitForCallAsync(TimeSpan.FromSeconds(30));
        delay.Release(TimeSpan.FromSeconds(30));

        await checkCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.WaitForCallAsync(TimeSpan.FromMinutes(30));
        monitor.Stop();
        await completion;
    }

    [Fact]
    public async Task Check_that_ignores_cancellation_keeps_the_single_flight_gate_and_does_not_block_stop()
    {
        var delay = new ControlledDelay();
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reportedFailure = null;
        var timeoutReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCount = 0;
        CancellationToken checkToken = default;
        using var monitor = new UpdateAvailabilityMonitor(async token =>
        {
            Interlocked.Increment(ref checkCount);
            checkToken = token;
            checkStarted.TrySetResult();
            await releaseCheck.Task;
            checkFinished.TrySetResult();
        }, error => { reportedFailure = error; timeoutReported.TrySetResult(); }, delay.DelayAsync);

        var completion = monitor.Start();
        await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.WaitForCallAsync(TimeSpan.FromSeconds(30));
        delay.Release(TimeSpan.FromSeconds(30));
        await delay.WaitForCallAsync(TimeSpan.FromMinutes(30));

        await timeoutReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<TimeoutException>(reportedFailure);
        Assert.True(checkToken.IsCancellationRequested);
        await monitor.CheckNowAsync();
        Assert.Equal(1, checkCount);

        monitor.Stop();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(checkFinished.Task.IsCompleted);

        releaseCheck.TrySetResult();
        await checkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Transient_check_failure_is_reported_and_a_later_tick_runs()
    {
        var delay = new ControlledDelay();
        Exception? failure = null;
        var failureReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCount = 0;
        using var monitor = new UpdateAvailabilityMonitor(_ =>
        {
            if (Interlocked.Increment(ref checkCount) == 1) throw new InvalidOperationException("temporary failure");
            secondCheck.TrySetResult();
            return Task.CompletedTask;
        }, error => { failure = error; failureReported.TrySetResult(); }, delay.DelayAsync);

        var completion = monitor.Start();
        await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("temporary failure", Assert.IsType<InvalidOperationException>(failure).Message);
        await delay.WaitForCallAsync(TimeSpan.FromMinutes(30));
        delay.Release(TimeSpan.FromMinutes(30));
        await secondCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, checkCount);

        monitor.Stop();
        await completion;
    }

    [Fact]
    public async Task Manual_check_after_dispose_is_a_no_op()
    {
        var checkCount = 0;
        var monitor = new UpdateAvailabilityMonitor(
            _ => { Interlocked.Increment(ref checkCount); return Task.CompletedTask; },
            _ => { });

        monitor.Dispose();
        await monitor.CheckNowAsync();

        Assert.Equal(0, checkCount);
        Assert.Throws<ObjectDisposedException>(() => { _ = monitor.Start(); });
    }

    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private readonly List<DelayCall> _calls = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var call = new DelayCall(delay);
            lock (_gate) _calls.Add(call);
            call.RegisterCancellation(cancellationToken);
            return call.Completion.Task;
        }

        public async Task WaitForCallAsync(TimeSpan delay)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_calls.Any(call => call.Delay == delay)) return;
                }
                await Task.Delay(10);
            }

            throw new TimeoutException($"No active delay for {delay} was requested.");
        }

        public void Release(TimeSpan delay)
        {
            lock (_gate)
            {
                var call = _calls.FirstOrDefault(candidate => candidate.Delay == delay && !candidate.Completion.Task.IsCompleted);
                Assert.NotNull(call);
                call!.Completion.TrySetResult();
            }
        }

        private sealed class DelayCall(TimeSpan delay)
        {
            public TimeSpan Delay { get; } = delay;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void RegisterCancellation(CancellationToken token) =>
                token.Register(() => Completion.TrySetCanceled(token));
        }
    }
}
