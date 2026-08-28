namespace RuBackend.Conversation;

/// <summary>
/// Prevents overlapping turns for one conversation while allowing unrelated
/// sessions to make progress independently.
/// </summary>
public sealed class ConversationSessionGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public int ActiveSessionCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public IDisposable? TryEnter(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        var key = sessionId.Trim();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.References++;
            if (!entry.Semaphore.Wait(0))
            {
                ReleaseReferenceUnsafe(key, entry);
                return null;
            }

            return new Lease(this, key, entry);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            // Active leases own their semaphore until they are released. Clearing
            // the lookup table prevents new callers while allowing those leases to
            // finish without racing disposal of a semaphore they still use.
            _entries.Clear();
        }
    }

    private void Release(string key, Entry entry)
    {
        lock (_sync)
        {
            entry.Semaphore.Release();
            ReleaseReferenceUnsafe(key, entry);
        }
    }

    private void ReleaseReferenceUnsafe(string key, Entry entry)
    {
        entry.References--;
        if (entry.References == 0)
        {
            if (_entries.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
            }

            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References;
    }

    private sealed class Lease : IDisposable
    {
        private ConversationSessionGate? _owner;
        private readonly string _key;
        private readonly Entry _entry;

        public Lease(ConversationSessionGate owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_key, _entry);
        }
    }
}
