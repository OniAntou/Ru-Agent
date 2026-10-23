namespace RuBackend.Harness.Subagents;

/// <summary>Serializes work for one key while allowing unrelated keys to proceed independently.</summary>
internal sealed class KeyedAsyncLock<TKey> : IDisposable where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private bool _disposed;

    public async ValueTask<IDisposable> AcquireAsync(TKey key, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
            else
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }
            entry.References++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            RemoveReference(key, entry);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Gate.Release();
        RemoveReference(key, entry);
    }

    private void RemoveReference(TKey key, Entry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _entries.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(KeyedAsyncLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private KeyedAsyncLock<TKey>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
    }
}
