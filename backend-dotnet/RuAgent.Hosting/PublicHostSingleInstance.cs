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
    private Mutex? _mutex;
    private bool _hasHandle;
    private bool _disposed;

    public string Name { get; }

    public PublicHostLockState State { get; }

    public bool IsAcquired => _hasHandle && !_disposed;

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
                _mutex = mutex;
                _hasHandle = true;
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
        if (_hasHandle)
        {
            try { _mutex?.ReleaseMutex(); } catch (Exception) { }
            _hasHandle = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
