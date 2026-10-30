namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Remembers which paths a session already read, so a read page can be counted as a
/// repeat. Bounded and counting-only: it never withholds content. A dedup decision
/// needs compaction-aware state the Host owns, so this tracker must not be used to
/// answer "unchanged" — only to measure how often the question would have come up.
/// </summary>
internal sealed class ReadRepeatTracker(int capacity = 2048)
{
    private const int MaximumKeyChars = 4224;

    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly int _capacity = Math.Max(1, capacity);

    /// <summary>
    /// True when this session already read <paramref name="path"/>. The first call
    /// records it; an over-long path is not tracked and counts as a first read.
    /// </summary>
    public bool Observe(string sessionId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumKeyChars)
        {
            return false;
        }

        var key = sessionId + "\n" + path;
        lock (_gate)
        {
            if (!_seen.Add(key))
            {
                return true;
            }

            _order.Enqueue(key);
            while (_order.Count > _capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return false;
        }
    }
}
