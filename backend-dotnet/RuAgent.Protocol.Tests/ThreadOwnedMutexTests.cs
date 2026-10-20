using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class ThreadOwnedMutexTests
{
    [Fact]
    public async Task Concurrent_disposal_of_nonowned_mutex_is_idempotent()
    {
        var name = "RuThreadOwnedMutex-" + Guid.NewGuid().ToString("N");
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        var ownerThread = new Thread(() =>
        {
            using var owner = new Mutex(initiallyOwned: true, name);
            ownerReady.Set();
            releaseOwner.Wait();
            owner.ReleaseMutex();
        }) { IsBackground = true };
        ownerThread.Start();
        try
        {
            Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)), "named mutex owner did not start");
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var lease = await ThreadOwnedMutex.AcquireAsync(name, mutexName =>
                    new Mutex(initiallyOwned: false, name: mutexName));
                Assert.False(lease.IsAcquired);

                var disposals = Enumerable.Range(0, 4)
                    .Select(_ => Task.Run(async () => await lease.DisposeAsync()))
                    .ToArray();
                await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            releaseOwner.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)), "named mutex owner did not release");
        }
    }
}
