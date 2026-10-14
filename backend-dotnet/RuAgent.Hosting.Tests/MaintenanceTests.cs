using Xunit;
namespace RuAgent.Hosting.Tests;
public sealed class MaintenanceTests
{
    [Fact]
    public void Busy_gate_does_not_block_other_requests()
    {
        var activity = new HostActivityTracker(TimeProvider.System);
        activity.Enter(); activity.Enter();
        Assert.False(activity.TryBeginMaintenance());
        Assert.True(activity.TryEnter());
        activity.Exit(); activity.Exit(); activity.Exit();
    }
    [Fact]
    public void Maintenance_blocks_admission_until_busy_check_is_released()
    {
        var activity = new HostActivityTracker(TimeProvider.System);
        activity.Enter(); Assert.True(activity.TryBeginMaintenance());
        Assert.False(activity.TryEnter()); Assert.False(activity.TryBeginMaintenance());
        activity.CancelMaintenance(); Assert.True(activity.TryEnter());
        activity.Exit(); activity.Exit(); Assert.Equal(0,activity.OpenRequests);
    }
}
