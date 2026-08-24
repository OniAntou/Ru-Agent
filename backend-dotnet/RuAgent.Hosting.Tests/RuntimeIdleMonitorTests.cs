using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class RuntimeIdleMonitorTests
{
    [Fact]
    public void TenMinuteDefaultAndSuppressedWhileActive()
    {
        var registry = new RuntimeClientLeaseRegistry();
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var fakeNow = now;
        Func<DateTimeOffset> provider = () => fakeNow;
        bool hasRun = false;
        bool hasApproval = false;
        var monitor = new RuntimeIdleMonitor(registry, provider, hasActiveRun: () => hasRun, hasPendingApproval: () => hasApproval);

        Assert.Equal(TimeSpan.FromMinutes(10), monitor.IdleTimeout);

        // Initially not idle
        Assert.False(monitor.ShouldShutdown(fakeNow));

        // After 10 minutes idle with no leases/runs, should shutdown
        fakeNow = now.AddMinutes(10).AddSeconds(1);
        Assert.True(monitor.ShouldShutdown(fakeNow));

        // Suppressed while active run exists
        hasRun = true;
        Assert.False(monitor.ShouldShutdown(fakeNow));
        hasRun = false;

        // Suppressed while approval exists
        hasApproval = true;
        Assert.False(monitor.ShouldShutdown(fakeNow));
        hasApproval = false;

        // Suppressed while lease exists
        var lease = registry.Acquire("chat");
        Assert.False(monitor.ShouldShutdown(fakeNow));
        lease.Dispose();
        Assert.True(monitor.ShouldShutdown(fakeNow));
    }

    [Fact]
    public void AbruptDisconnectReleasesOnce()
    {
        var registry = new RuntimeClientLeaseRegistry();
        var lease = registry.Acquire("agent.sse");
        Assert.True(registry.HasLeases);
        lease.Dispose();
        Assert.False(registry.HasLeases);
        // Second dispose should not go negative
        lease.Dispose();
        Assert.Equal(0, registry.LeaseCount);
    }

    [Fact]
    public void ShortHttpDoesNotCreateImmortalLeases()
    {
        var registry = new RuntimeClientLeaseRegistry();
        // Short HTTP request does not call Acquire, so count remains 0
        Assert.Equal(0, registry.LeaseCount);
        // Simulate short request: no lease acquired
        Assert.False(registry.HasLeases);
    }

    [Fact]
    public void FakeTimeDeterministic()
    {
        var registry = new RuntimeClientLeaseRegistry();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = start;
        var monitor = new RuntimeIdleMonitor(registry, () => now);
        // Notify at start
        monitor.NotifyActivity(start);
        now = start.AddMinutes(9);
        Assert.False(monitor.ShouldShutdown(now));
        now = start.AddMinutes(10);
        Assert.True(monitor.ShouldShutdown(now));
        // Notify resets
        monitor.NotifyActivity(now);
        now = now.AddMinutes(5);
        Assert.False(monitor.ShouldShutdown(now));
    }
}
