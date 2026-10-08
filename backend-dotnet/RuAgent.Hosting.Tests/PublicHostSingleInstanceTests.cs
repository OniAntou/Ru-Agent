using System.Threading;
using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class PublicHostSingleInstanceTests
{
    [Fact]
    public void Claim_survives_the_thread_that_created_the_host_guard()
    {
        var name = "Local\\RuTestLifetime-" + Guid.NewGuid().ToString("N");
        PublicHostSingleInstance? owner = null;
        var creatingThread = new Thread(() => owner = new PublicHostSingleInstance(name));
        creatingThread.Start();
        Assert.True(creatingThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(owner!.IsAcquired);
        try
        {
            using var contender = new PublicHostSingleInstance(name);
            Assert.Equal(PublicHostLockState.HeldByAnother, contender.State);
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directory_aliases_share_the_same_root_claim(bool isLogRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ru-root-alias-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(real);
        using var setup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = $"/c mklink /J \"{alias}\" \"{real}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(setup.WaitForExit(5000));
        Assert.Equal(0, setup.ExitCode);
        Func<string, PublicHostSingleInstance> acquire = isLogRoot
            ? PublicHostSingleInstance.AcquireForLogRoot : PublicHostSingleInstance.AcquireForDataRoot;
        var (thread, release) = HoldOnWorkerThread(() => acquire(real));
        try
        {
            using var contender = acquire(alias);
            Assert.Equal(PublicHostLockState.HeldByAnother, contender.State);
            Assert.False(contender.IsAcquired);
        }
        finally
        {
            release.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Directory.Delete(alias);
            Directory.Delete(root, recursive: true);
        }
    }

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
            var held = new Mutex(initiallyOwned: false, name);
            Assert.True(held.WaitOne(TimeSpan.Zero));
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

    [Fact]
    public void DataRootClaimIsExclusiveAcrossThreads()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var (thread, release) = HoldOnWorkerThread(() => PublicHostSingleInstance.AcquireForDataRoot(dataRoot));
        try
        {
            using var second = PublicHostSingleInstance.AcquireForDataRoot(dataRoot);
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
    public void DataRootClaimCanonicalizesPathSpelling()
    {
        // Windows paths are case-insensitive and tolerate a trailing separator;
        // two hosts that name the same root differently must still conflict.
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var (thread, release) = HoldOnWorkerThread(() => PublicHostSingleInstance.AcquireForDataRoot(dataRoot));
        try
        {
            using var second = PublicHostSingleInstance.AcquireForDataRoot(
                dataRoot.ToUpperInvariant() + Path.DirectorySeparatorChar);
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
    public void SameDataRootOnAnotherPortFailsClosed()
    {
        // The single-instance lock used to be per port only: a second host on a
        // different port acquired its own mutex and then clobbered the shared
        // runtime descriptor and Broker session files. The data root now decides.
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var firstPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        var secondPort = firstPort;
        while (secondPort == firstPort)
        {
            secondPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        }

        var (thread, release) = HoldOnWorkerThread(
            () => PublicHostSingleInstance.AcquireForPortAndDataRoot(firstPort, dataRoot, logRoot));
        try
        {
            using var second = PublicHostSingleInstance.AcquireForPortAndDataRoot(secondPort, dataRoot, logRoot);
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
    public void DifferentDataRootsDoNotConflict()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var firstLogRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var secondLogRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var firstPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        var secondPort = firstPort;
        while (secondPort == firstPort)
        {
            secondPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        }

        using var first = PublicHostSingleInstance.AcquireForPortAndDataRoot(firstPort, firstRoot, firstLogRoot);
        using var second = PublicHostSingleInstance.AcquireForPortAndDataRoot(secondPort, secondRoot, secondLogRoot);
        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public void SameLogRootOnAnotherDataRootFailsClosed()
    {
        // Every managed host writes its Broker session descriptor under the log
        // root. Distinct data roots must not let a second host reuse that path.
        var firstRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var firstPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        var secondPort = firstPort;
        while (secondPort == firstPort)
        {
            secondPort = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        }

        var (thread, release) = HoldOnWorkerThread(
            () => PublicHostSingleInstance.AcquireForPortAndDataRoot(firstPort, firstRoot, logRoot));
        try
        {
            using var second = PublicHostSingleInstance.AcquireForPortAndDataRoot(secondPort, secondRoot, logRoot);
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
    public async Task FailedLogRootClaimReleasesTheDataRoot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-root-" + Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var otherLogRoot = Path.Combine(Path.GetTempPath(), "ru-logs-" + Guid.NewGuid().ToString("N"));
        var port = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        var (thread, release) = HoldOnWorkerThread(() => PublicHostSingleInstance.AcquireForLogRoot(logRoot));
        try
        {
            using var denied = PublicHostSingleInstance.AcquireForPortAndDataRoot(port, dataRoot, logRoot);
            Assert.False(denied.IsAcquired);
            Assert.Equal(PublicHostLockState.HeldByAnother, denied.State);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }

        // Mutex ownership is per-thread: the retry must run elsewhere, or a claim
        // leaked on this thread would be re-entered silently.
        var retryAcquired = await Task.Run(() =>
        {
            using var retry = PublicHostSingleInstance.AcquireForPortAndDataRoot(port, dataRoot, otherLogRoot);
            return retry.IsAcquired;
        });
        Assert.True(retryAcquired);
    }

    private static (Thread Thread, ManualResetEventSlim Release) HoldOnWorkerThread(string name)
        => HoldOnWorkerThread(() => new PublicHostSingleInstance(name));

    private static (Thread Thread, ManualResetEventSlim Release) HoldOnWorkerThread(
        Func<PublicHostSingleInstance> acquire)
    {
        // Mutex ownership is per-thread, so a second handle on the acquiring
        // thread would recursively succeed. The holder must live on its own thread.
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        PublicHostSingleInstance? instance = null;
        var thread = new Thread(() =>
        {
            instance = acquire();
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
