namespace RuAgent.Hosting;

public sealed class RuntimeClientLeaseRegistry
{
    private int _count;
    private readonly object _gate = new();

    public int LeaseCount
    {
        get { lock (_gate) return _count; }
    }

    public bool HasLeases
    {
        get { lock (_gate) return _count > 0; }
    }

    public IDisposable Acquire(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (kind is not ("chat" or "sse" or "agent.sse" or "websocket"))
        {
            // Allow any non-empty kind for flexibility; but expected are chat/sse
        }
        lock (_gate) _count++;
        return new Lease(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_count > 0) _count--;
        }
    }

    private sealed class Lease : IDisposable
    {
        private RuntimeClientLeaseRegistry? _registry;
        private int _disposed;

        public Lease(RuntimeClientLeaseRegistry registry) => _registry = registry;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                var r = Interlocked.Exchange(ref _registry, null);
                r?.Release();
            }
        }
    }
}
