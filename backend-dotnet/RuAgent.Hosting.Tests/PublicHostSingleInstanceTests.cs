using System.Threading;
using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class PublicHostSingleInstanceTests
{
    [Fact]
    public void SecondAcquirerOnAnotherThreadIsNotOwnedAndNeverBlocks()
    {
        // The contention that matters is cross-process; cross-thread is the
        // same zero-wait miss path. The old constructor blocked forever on a
        // mutex another process held -- acquisition must now fail fast.
        var name = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        using var first = new PublicHostSingleInstance(name);
        Assert.True(first.IsAcquired);

        PublicHostSingleInstance? second = null;
        var worker = new Thread(() =>
        {
            second = new PublicHostSingleInstance(name);
            second.Dispose();
        })
        { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "contender blocked instead of failing fast");
        Assert.NotNull(second);
        Assert.False(second!.IsAcquired);
    }

    [Fact]
    public void ReleaseAllowsReacquire()
    {
        var name = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        var first = new PublicHostSingleInstance(name);
        Assert.True(first.IsAcquired);
        first.Dispose();

        using var second = new PublicHostSingleInstance(name);
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public void AbandonedMutexIsAdoptedByTheNextInstance()
    {
        // A host that dies without releasing must not wedge the next start:
        // .NET hands ownership to the abandoned waiter, and the instance must
        // report acquired instead of throwing out of the constructor.
        var name = "Local\\RuTestAbandoned-" + Guid.NewGuid().ToString("N");
        var owner = new Thread(() =>
        {
            // Owned here and deliberately never released: thread exit abandons
            // the mutex.
            var held = new PublicHostSingleInstance(name);
            Assert.True(held.IsAcquired);
        })
        { IsBackground = true };
        owner.Start();
        owner.Join(TimeSpan.FromSeconds(5));

        using var adopter = new PublicHostSingleInstance(name);
        Assert.True(adopter.IsAcquired);
    }

    [Fact]
    public void DifferentNamesDoNotConflict()
    {
        var name1 = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        var name2 = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        using var a = new PublicHostSingleInstance(name1);
        using var b = new PublicHostSingleInstance(name2);
        Assert.True(a.IsAcquired);
        Assert.True(b.IsAcquired);
    }
}
