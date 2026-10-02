using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Hosting;
using Xunit;

namespace RuBackend.Tests.Hosting;

public sealed class HostIdleShutdownTests
{
    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("-1", 10)]
    [InlineData(" 3 ", 3)]
    [InlineData("2.5", 2.5)]
    public void ResolveWindow_ParsesMinutesOrKeepsTheDefault(string? raw, double expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), HostIdleShutdownService.ResolveWindow(raw));

    [Fact]
    public void ResolveWindow_ZeroDisablesTheShutdown()
    {
        Assert.Equal(TimeSpan.Zero, HostIdleShutdownService.ResolveWindow("0"));
    }

    [Fact]
    public void ShouldShutdown_RequiresAWindowNoInflightRequestsNoWorkAndAStaleClock()
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromMinutes(10);

        Assert.True(HostActivityTracker.ShouldShutdown(now, now.AddMinutes(-11), 0, 0, window));
        Assert.False(HostActivityTracker.ShouldShutdown(now, now.AddMinutes(-9), 0, 0, window));
        Assert.False(HostActivityTracker.ShouldShutdown(now, now.AddMinutes(-11), 1, 0, window));
        Assert.False(HostActivityTracker.ShouldShutdown(now, now.AddMinutes(-11), 0, 1, window));
        Assert.False(HostActivityTracker.ShouldShutdown(now, now.AddMinutes(-11), 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Tracker_CountsRequestsAndTouchesTheClock()
    {
        var clock = new TestClock();
        var tracker = new HostActivityTracker(clock);
        var startedAt = tracker.LastActivityUtc;

        clock.Now = clock.Now.AddSeconds(5);
        tracker.Enter();
        Assert.Equal(1, tracker.OpenRequests);
        Assert.Equal(clock.Now, tracker.LastActivityUtc);
        Assert.True(tracker.LastActivityUtc > startedAt);

        tracker.Exit();
        Assert.Equal(0, tracker.OpenRequests);
    }

    [Fact]
    public async Task Service_StopsTheHostAfterTheIdleWindow()
    {
        var clock = new TestClock();
        var tracker = new HostActivityTracker(clock);
        var lifetime = new RecordingLifetime();
        var service = NewService(clock, tracker, lifetime, probe: new FixedProbe());

        await service.StartAsync(CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(2);

        Assert.True(await WaitUntil(() => lifetime.Stopped), "an idle host must stop itself");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Service_WaitsForOpenRequestsAndActiveWork()
    {
        var clock = new TestClock();
        var tracker = new HostActivityTracker(clock);
        var lifetime = new RecordingLifetime();
        var probe = new FixedProbe { ActiveWorkCount = 1 };
        var service = NewService(clock, tracker, lifetime, probe);

        tracker.Enter();
        await service.StartAsync(CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(2);

        await Task.Delay(80);
        Assert.False(lifetime.Stopped);

        // A streaming response just ended, but the run it belongs to is still registered.
        tracker.Exit();
        await Task.Delay(80);
        Assert.False(lifetime.Stopped);

        probe.ActiveWorkCount = 0;
        Assert.True(await WaitUntil(() => lifetime.Stopped), "once the run leaves, the host may idle out");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Service_WithADisabledWindowNeverStops()
    {
        var clock = new TestClock();
        var tracker = new HostActivityTracker(clock);
        var lifetime = new RecordingLifetime();
        var service = new HostIdleShutdownService(
            lifetime,
            tracker,
            NullLogger<HostIdleShutdownService>.Instance,
            clock,
            new FixedProbe(),
            window: TimeSpan.Zero,
            pollInterval: TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        clock.Now = clock.Now.AddHours(1);
        await Task.Delay(80);

        Assert.False(lifetime.Stopped);
        await service.StopAsync(CancellationToken.None);
    }

    private static HostIdleShutdownService NewService(
        TestClock clock,
        HostActivityTracker tracker,
        RecordingLifetime lifetime,
        FixedProbe probe) =>
        new(
            lifetime,
            tracker,
            NullLogger<HostIdleShutdownService>.Instance,
            clock,
            probe,
            window: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopping.Token;

        public void StopApplication()
        {
            Stopped = true;
            _stopping.Cancel();
        }
    }

    private sealed class FixedProbe : IHostIdleProbe
    {
        public int ActiveWorkCount { get; set; }
    }
}
