namespace RuAgent.Runtime.Core.Lifecycle;

public enum ResourceKind
{
    GpuAudio,
    VtubeStudio,
    Observation,
}

public sealed class RuntimeLeaseCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<ResourceKind, SemaphoreSlim> _gates =
        new Dictionary<ResourceKind, SemaphoreSlim>
        {
            [ResourceKind.GpuAudio] = new SemaphoreSlim(1, 1),
            [ResourceKind.VtubeStudio] = new SemaphoreSlim(1, 1),
            [ResourceKind.Observation] = new SemaphoreSlim(1, 1),
        };
    private int _disposed;

    public async ValueTask<RuntimeLease> AcquireAsync(ResourceKind kind, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_gates.TryGetValue(kind, out var gate))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeLease(gate);
    }

    public bool TryAcquire(ResourceKind kind, out RuntimeLease? lease)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_gates.TryGetValue(kind, out var gate))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!gate.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new RuntimeLease(gate);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (var gate in _gates.Values)
            {
                gate.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class RuntimeLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate;
    private int _released;

    internal RuntimeLease(SemaphoreSlim gate) => _gate = gate;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _gate.Release();
        }

        return ValueTask.CompletedTask;
    }
}
