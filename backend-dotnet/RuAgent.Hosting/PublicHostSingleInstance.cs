using System.Threading;

namespace RuAgent.Hosting;

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
        bool createdNew;
        var mutex = new Mutex(initiallyOwned: true, name: name, out createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            _hasHandle = true;
        }
        else
        {
            // Mutex already exists - we do not own it
            try { mutex.Dispose(); } catch { }
            _mutex = null;
            _hasHandle = false;
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
            try { _mutex?.ReleaseMutex(); } catch { }
            _hasHandle = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
