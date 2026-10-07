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

    [Fact]
    public void HeldMutexIsReportedAsHeldNotAsAccessDenied()
    {
        var name = "Local\\RuTestHeld-" + Guid.NewGuid().ToString("N");
        var (thread, release) = HoldOnWorkerThread(name);
        try
        {
            using var second = new PublicHostSingleInstance(name);
            Assert.False(second.IsAcquired);
            Assert.Equal(PublicHostLockState.HeldByAnother, second.State);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AccessDeniedNameIsReportedAsAccessDenied()
    {
        var name = "Local\\RuTestDenied-" + Guid.NewGuid().ToString("N");
        using var denied = new PublicHostSingleInstance(name, _ => throw new UnauthorizedAccessException());
        Assert.False(denied.IsAcquired);
        Assert.Equal(PublicHostLockState.AccessDenied, denied.State);
    }

    [Fact]
    public void HeldGlobalLockDoesNotFallBackToLocal()
    {
        var globalName = "Global\\RuTestFallback-" + Guid.NewGuid().ToString("N");
        var localName = "Local\\RuTestFallback-" + Guid.NewGuid().ToString("N");
        var heldName = "Local\\RuTestHeldGlobal-" + Guid.NewGuid().ToString("N");
        var (thread, release) = HoldOnWorkerThread(heldName);
        try
        {
            using var candidate = PublicHostSingleInstance.AcquireWithLocalFallback(
                globalName,
                localName,
                name => name == globalName
                    ? new PublicHostSingleInstance(heldName)
                    : new PublicHostSingleInstance(localName));

            Assert.Equal(PublicHostLockState.HeldByAnother, candidate.State);
            Assert.False(candidate.IsAcquired);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AccessDeniedGlobalLockFallsBackToLocal()
    {
        var globalName = "Global\\RuTestFallback-" + Guid.NewGuid().ToString("N");
        var localName = "Local\\RuTestFallback-" + Guid.NewGuid().ToString("N");

        using var candidate = PublicHostSingleInstance.AcquireWithLocalFallback(
            globalName,
            localName,
            name => name == globalName
                ? new PublicHostSingleInstance(name, _ => throw new UnauthorizedAccessException())
                : new PublicHostSingleInstance(name));

        Assert.Equal(localName, candidate.Name);
        Assert.Equal(PublicHostLockState.Acquired, candidate.State);
        Assert.True(candidate.IsAcquired);
    }

    [Fact]
    public void AccessDeniedGlobalLockFailsClosedWhenLocalIsHeld()
    {
        var globalName = "Global\\RuTestFallback-" + Guid.NewGuid().ToString("N");
        var localName = "Local\\RuTestFallback-" + Guid.NewGuid().ToString("N");
        var (thread, release) = HoldOnWorkerThread(localName);
        try
        {
            using var candidate = PublicHostSingleInstance.AcquireWithLocalFallback(
                globalName,
                localName,
                name => name == globalName
                    ? new PublicHostSingleInstance(name, _ => throw new UnauthorizedAccessException())
                    : new PublicHostSingleInstance(name));

            Assert.Equal(localName, candidate.Name);
            Assert.Equal(PublicHostLockState.HeldByAnother, candidate.State);
            Assert.False(candidate.IsAcquired);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static (Thread Thread, ManualResetEventSlim Release) HoldOnWorkerThread(string name)
    {
        // Mutex ownership is per-thread, so a second handle on the acquiring
        // thread would recursively succeed. The holder must live on its own thread.
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        PublicHostSingleInstance? instance = null;
        var thread = new Thread(() =>
        {
            instance = new PublicHostSingleInstance(name);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            instance.Dispose();
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder thread did not acquire");
        Assert.True(instance!.IsAcquired);
        return (thread, release);
    }
}
