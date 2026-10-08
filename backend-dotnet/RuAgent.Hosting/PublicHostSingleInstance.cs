using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RuAgent.Hosting;

/// <summary>
/// How one zero-wait named-mutex acquisition ended.
/// </summary>
public enum PublicHostLockState
{
    /// <summary>This instance owns the lock.</summary>
    Acquired = 0,

    /// <summary>Another process holds the lock.</summary>
    HeldByAnother = 1,

    /// <summary>The name cannot be created or opened by this account (for
    /// example <c>Global\</c> without SeCreateGlobalPrivilege).</summary>
    AccessDenied = 2,
}

/// <summary>
/// Non-blocking named-mutex single-instance guard.
/// </summary>
/// <remarks>
/// The old form used <c>new Mutex(initiallyOwned: true, name, out _)</c>, whose
/// constructor *blocks* until it owns a mutex another process already holds: a
/// second host hung in startup instead of reporting "already running", and an
/// owner that crashed left an abandoned mutex that threw out of the next
/// constructor uncaught. Acquisition is now an explicit zero-wait attempt, an
/// abandoned mutex is adopted (its previous owner is gone by definition), and
/// the result distinguishes a lock another process holds from a name this
/// account cannot open. Only the latter may fall back to another namespace: a
/// held lock fails closed instead of silently starting a second host that
/// clobbers the live instance.
/// </remarks>
public sealed class PublicHostSingleInstance : IDisposable
{
    private readonly List<Mutex> _mutexes = new();
    private bool _disposed;

    public string Name { get; }

    public PublicHostLockState State { get; }

    public bool IsAcquired => _mutexes.Count > 0 && !_disposed;

    public PublicHostSingleInstance(string name)
        : this(name, static mutexName => new Mutex(initiallyOwned: false, name: mutexName, out _))
    {
    }

    internal PublicHostSingleInstance(string name, Func<string, Mutex> openMutex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(openMutex);
        Name = name;
        Mutex? mutex = null;
        try
        {
            mutex = openMutex(name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing. .NET hands us
                // ownership on the abandoned wait, so adopt it.
                owned = true;
            }

            if (owned)
            {
                _mutexes.Add(mutex);
                State = PublicHostLockState.Acquired;
            }
            else
            {
                State = PublicHostLockState.HeldByAnother;
                mutex.Dispose();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The name cannot be created or opened by this account. Nothing is
            // held; the caller may pick another namespace.
            State = PublicHostLockState.AccessDenied;
            if (mutex is not null)
            {
                try { mutex.Dispose(); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Acquires the global lock, falling back to the local namespace only when
    /// the global name cannot be opened by this account. A lock another process
    /// holds is returned as-is so the caller can fail closed.
    /// </summary>
    public static PublicHostSingleInstance AcquireWithLocalFallback(string globalName, string localName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        return AcquireWithLocalFallback(
            globalName,
            localName,
            static name => new PublicHostSingleInstance(name));
    }

    /// <summary>
    /// Claims the lock for one canonical data root. Every managed host publishes
    /// its runtime descriptor under the root, so hosts on different ports still
    /// conflict there.
    /// </summary>
    public static PublicHostSingleInstance AcquireForDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var key = ComputeRootKey(dataRoot);
        return AcquireWithLocalFallback(
            $"Global\\RuPublicHostDataRoot-{key}",
            $"Local\\RuPublicHostDataRoot-{key}");
    }

    /// <summary>
    /// Claims the lock for one canonical log root. Every managed host writes its
    /// Broker session descriptor under the log root, so hosts with different data
    /// roots still conflict there.
    /// </summary>
    public static PublicHostSingleInstance AcquireForLogRoot(string logRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        var key = ComputeRootKey(logRoot);
        return AcquireWithLocalFallback(
            $"Global\\RuPublicHostLogRoot-{key}",
            $"Local\\RuPublicHostLogRoot-{key}");
    }

    /// <summary>
    /// Claims the shared data root, the shared log root, and the public port
    /// together, so one guard releases all three for the host lifetime. The roots
    /// are claimed first: a second host must fail before it can start a Broker or
    /// publish a descriptor over the live instance's files.
    /// </summary>
    public static PublicHostSingleInstance AcquireForPortAndDataRoot(int publicPort, string dataRoot, string logRoot)
    {
        var dataRootGuard = AcquireForDataRoot(dataRoot);
        if (!dataRootGuard.IsAcquired)
        {
            return dataRootGuard;
        }

        var logRootGuard = AcquireForLogRoot(logRoot);
        if (!logRootGuard.IsAcquired)
        {
            dataRootGuard.Dispose();
            return logRootGuard;
        }

        var portGuard = AcquireWithLocalFallback(
            $"Global\\RuPublicHost-{publicPort}",
            $"Local\\RuPublicHost-{publicPort}");
        if (!portGuard.IsAcquired)
        {
            dataRootGuard.Dispose();
            logRootGuard.Dispose();
            return portGuard;
        }

        portGuard.TakeMutexesFrom(dataRootGuard);
        portGuard.TakeMutexesFrom(logRootGuard);
        return portGuard;
    }

    internal static PublicHostSingleInstance AcquireWithLocalFallback(
        string globalName,
        string localName,
        Func<string, PublicHostSingleInstance> acquire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        ArgumentNullException.ThrowIfNull(acquire);
        var global = acquire(globalName);
        if (global.State != PublicHostLockState.AccessDenied)
        {
            return global;
        }

        global.Dispose();
        return acquire(localName);
    }

    public static bool TryAcquire(string name, out PublicHostSingleInstance? instance)
    {
        var candidate = new PublicHostSingleInstance(name);
        if (candidate.IsAcquired)
        {
            instance = candidate;
            return true;
        }

        candidate.Dispose();
        instance = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var mutex in _mutexes)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
            mutex.Dispose();
        }

        _mutexes.Clear();
    }

    private void TakeMutexesFrom(PublicHostSingleInstance other)
    {
        _mutexes.AddRange(other._mutexes);
        other._mutexes.Clear();
        other._disposed = true;
    }

    private static string ComputeRootKey(string root)
    {
        var canonical = Path.GetFullPath(root.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
