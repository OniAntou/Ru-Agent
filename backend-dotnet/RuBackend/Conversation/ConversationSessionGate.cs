using System.Collections.Concurrent;

namespace RuBackend.Conversation;

/// <summary>
/// Prevents overlapping turns for one conversation while allowing unrelated
/// sessions to make progress independently.
/// </summary>
public sealed class ConversationSessionGate : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public int ActiveSessionCount => _entries.Count;

    public IDisposable? TryEnter(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        var key = sessionId.Trim();
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        Interlocked.Increment(ref entry.References);
        if (!entry.Semaphore.Wait(0))
        {
            ReleaseReference(key, entry);
            return null;
        }

        return new Lease(this, key, entry);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            entry.Semaphore.Dispose();
        }

        _entries.Clear();
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(string key, Entry entry)
    {
        if (Interlocked.Decrement(ref entry.References) == 0)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
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
