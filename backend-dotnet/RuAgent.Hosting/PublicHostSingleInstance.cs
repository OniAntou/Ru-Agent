using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RuAgent.Protocol;

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
    private readonly List<ThreadOwnedMutex> _mutexes = new();
    private readonly List<DirectoryHandleLease> _rootLeases = new();
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
        ThreadOwnedMutex? mutex = null;
        try
        {
            mutex = ThreadOwnedMutex.Acquire(name, openMutex);
            if (mutex.IsAcquired)
            {
                _mutexes.Add(mutex);
                State = PublicHostLockState.Acquired;
            }
            else
            {
                State = mutex.IsAccessDenied ? PublicHostLockState.AccessDenied : PublicHostLockState.HeldByAnother;
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
        return AcquireRoot(dataRoot, "DataRoot");
    }

    /// <summary>
    /// Claims the lock for one canonical log root. Every managed host creates its
    /// in-process execution session under the log root, so hosts with different
    /// data roots still conflict there.
    /// </summary>
    public static PublicHostSingleInstance AcquireForLogRoot(string logRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        return AcquireRoot(logRoot, "LogRoot");
    }

    /// <summary>
    /// Claims the shared data root, the shared log root, and the public port
    /// together, so one guard releases all three for the host lifetime. The roots
    /// are claimed first: a second host must fail before it can compose its
    /// in-process execution session or publish a descriptor over the live
    /// instance's files.
    /// </summary>
    public static PublicHostSingleInstance AcquireForPortAndDataRoot(int publicPort, string dataRoot, string logRoot)
    {
        var dataRootGuard = AcquireForDataRoot(dataRoot);
        if (!dataRootGuard.IsAcquired)
        {
            return dataRootGuard;
        }

        PublicHostSingleInstance? logRootGuard = null;
        try
        {
            logRootGuard = AcquireForLogRoot(logRoot);
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
        catch
        {
            dataRootGuard.Dispose();
            logRootGuard?.Dispose();
            throw;
        }
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
            mutex.Dispose();
        }

        _mutexes.Clear();
        foreach (var root in _rootLeases) root.Dispose();
        _rootLeases.Clear();
    }

    private void TakeMutexesFrom(PublicHostSingleInstance other)
    {
        _mutexes.AddRange(other._mutexes);
        _rootLeases.AddRange(other._rootLeases);
        other._mutexes.Clear();
        other._rootLeases.Clear();
        other._disposed = true;
    }

    private static PublicHostSingleInstance AcquireRoot(string root, string kind)
    {
        var lease = DirectoryHandleLease.Open(root.Trim(), createMissing: true);
        try
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(lease.Identity));
            var key = Convert.ToHexString(digest).ToLowerInvariant();
            var guard = AcquireWithLocalFallback($"Global\\RuPublicHost{kind}-{key}", $"Local\\RuPublicHost{kind}-{key}");
            if (guard.IsAcquired) guard._rootLeases.Add(lease);
            else lease.Dispose();
            return guard;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
