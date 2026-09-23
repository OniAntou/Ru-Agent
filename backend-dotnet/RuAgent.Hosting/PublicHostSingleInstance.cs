using System.Threading;

namespace RuAgent.Hosting;

/// <summary>
/// Non-blocking named-mutex single-instance guard.
/// </summary>
/// <remarks>
/// The old form used <c>new Mutex(initiallyOwned: true, name, out _)</c>, whose
/// constructor *blocks* until it owns a mutex another process already holds: a
/// second host hung in startup instead of reporting "already running", and an
/// owner that crashed left an abandoned mutex that threw out of the next
/// constructor uncaught. Acquisition is now an explicit zero-wait attempt, an
/// abandoned mutex is adopted (its previous owner is gone by definition), and a
/// name the account cannot create (Global\ without SeCreateGlobalPrivilege)
/// surfaces as a normal "not acquired" so callers can fall back to Local\.
/// </remarks>
public sealed class PublicHostSingleInstance : IDisposable
{
    private Mutex? _mutex;
    private bool _hasHandle;
    private bool _disposed;

    public string Name { get; }

    public bool IsAcquired => _hasHandle && !_disposed;

    public PublicHostSingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: name, out _);
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
            }
            else
            {
                mutex.Dispose();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A name this account cannot create (or a held mutex this account
            // cannot open) is indistinguishable from "someone else owns it":
            // report not-acquired so the caller can pick another namespace.
            if (mutex is not null)
            {
                try { mutex.Dispose(); } catch (Exception) { }
            }
        }
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
