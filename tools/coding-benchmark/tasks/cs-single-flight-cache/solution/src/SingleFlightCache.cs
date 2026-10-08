namespace SingleFlight;

public sealed class SingleFlightCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Flight> _flights;
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<TKey> _recency = new();
    private readonly Func<TKey, CancellationToken, Task<TValue>> _loader;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly Dictionary<TKey, long> _keyGeneration;
    private long _clearGeneration;

    public SingleFlightCache(Func<TKey, CancellationToken, Task<TValue>> loader, TimeSpan ttl, int capacity, TimeProvider? clock = null)
        : this(loader, ttl, capacity, clock, null) { }

    public SingleFlightCache(Func<TKey, CancellationToken, Task<TValue>> loader, TimeSpan ttl, int capacity, TimeProvider? clock, IEqualityComparer<TKey>? comparer)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _loader = loader;
        _ttl = ttl;
        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;
        _flights = new Dictionary<TKey, Flight>(comparer);
        _entries = new Dictionary<TKey, Entry>(comparer);
        _keyGeneration = new Dictionary<TKey, long>(comparer);
    }

    public Task<TValue> GetAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        Flight flight;
        var shouldStart = false;
        lock (_gate)
        {
            if (TryGetCached(key, out var value)) return Task.FromResult(value);
            if (!_flights.TryGetValue(key, out flight!))
            {
                flight = new Flight(_clearGeneration, GetGeneration(key));
                _flights.Add(key, flight);
                shouldStart = true;
            }
        }
        if (shouldStart) _ = LoadAsync(key, flight);
        return flight.Completion.Task.WaitAsync(cancellationToken);
    }

    public void Invalidate(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _keyGeneration[key] = checked(GetGeneration(key) + 1);
            RemoveCached(key);
            _flights.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _clearGeneration = checked(_clearGeneration + 1);
            _entries.Clear();
            _recency.Clear();
            _flights.Clear();
        }
    }

    private async Task LoadAsync(TKey key, Flight flight)
    {
        try
        {
            var value = await _loader(key, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (IsCurrent(key, flight))
                {
                    _flights.Remove(key);
                    Store(key, value);
                }
            }
            flight.Completion.TrySetResult(value);
        }
        catch (Exception error)
        {
            lock (_gate)
                if (IsCurrent(key, flight)) _flights.Remove(key);
            flight.Completion.TrySetException(error);
            _ = flight.Completion.Task.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private bool IsCurrent(TKey key, Flight flight) => _clearGeneration == flight.ClearGeneration
        && GetGeneration(key) == flight.KeyGeneration
        && _flights.TryGetValue(key, out var active) && ReferenceEquals(active, flight);

    private bool TryGetCached(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (_clock.GetUtcNow() < entry.ExpiresAt)
            {
                _recency.Remove(entry.Node);
                _recency.AddLast(entry.Node);
                value = entry.Value;
                return true;
            }
            RemoveCached(key);
        }
        value = default!;
        return false;
    }

    private void Store(TKey key, TValue value)
    {
        RemoveCached(key);
        var node = _recency.AddLast(key);
        _entries.Add(key, new Entry(value, _clock.GetUtcNow() + _ttl, node));
        while (_entries.Count > _capacity)
        {
            var oldest = _recency.First!;
            _recency.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private void RemoveCached(TKey key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _recency.Remove(entry.Node);
    }

    private long GetGeneration(TKey key) => _keyGeneration.TryGetValue(key, out var generation) ? generation : 0;

    private sealed record Entry(TValue Value, DateTimeOffset ExpiresAt, LinkedListNode<TKey> Node);
    private sealed class Flight(long clearGeneration, long keyGeneration)
    {
        public long ClearGeneration { get; } = clearGeneration;
        public long KeyGeneration { get; } = keyGeneration;
        public TaskCompletionSource<TValue> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
