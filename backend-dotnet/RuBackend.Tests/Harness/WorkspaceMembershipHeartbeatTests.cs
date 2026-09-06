using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceMembershipHeartbeatTests
{
    [Fact]
    public async Task Lost_completes_when_renewal_is_rejected()
    {
        var renewals = 0;
        await using var heartbeat = WorkspaceMembershipHeartbeat.Start(
            _ => Task.FromResult(Interlocked.Increment(ref renewals) < 3),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        await heartbeat.Lost.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, Volatile.Read(ref renewals));
    }

    [Fact]
    public async Task Dispose_stops_successful_renewals()
    {
        var renewals = 0;
        var renewedTwice = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = WorkspaceMembershipHeartbeat.Start(
            _ =>
            {
                if (Interlocked.Increment(ref renewals) == 2)
                {
                    renewedTwice.TrySetResult();
                }

                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);
        await renewedTwice.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await heartbeat.DisposeAsync();
        var countAfterDispose = Volatile.Read(ref renewals);
        await Task.Delay(200);

        Assert.Equal(countAfterDispose, Volatile.Read(ref renewals));
        Assert.False(heartbeat.Lost.IsCompleted);
    }

    [Fact]
    public async Task Renewal_exception_is_reported_as_lost()
    {
        await using var heartbeat = WorkspaceMembershipHeartbeat.Start(
            _ => Task.FromException<bool>(new IOException("renew failed")),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        await heartbeat.Lost.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(heartbeat.Lost.IsCompletedSuccessfully);
    }
}
