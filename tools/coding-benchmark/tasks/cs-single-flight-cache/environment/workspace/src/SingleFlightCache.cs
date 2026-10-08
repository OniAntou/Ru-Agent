using System.Collections.Concurrent;

namespace SingleFlight;

public sealed class SingleFlightCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _flights = new();
    private readonly Func<TKey, CancellationToken, Task<TValue>> _loader;
    public SingleFlightCache(Func<TKey, CancellationToken, Task<TValue>> loader, TimeSpan ttl, int capacity, TimeProvider? clock = null)
    {
        _loader = loader;
    }
    public async Task<TValue> GetAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var lazy = _flights.GetOrAdd(key, value => new Lazy<Task<TValue>>(() => _loader(value, CancellationToken.None)));
        try { return await lazy.Value.WaitAsync(cancellationToken); }
        catch { _flights.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, lazy)); throw; }
    }
    public void Invalidate(TKey key) => _flights.TryRemove(key, out _);
    public void Clear() => _flights.Clear();
}
