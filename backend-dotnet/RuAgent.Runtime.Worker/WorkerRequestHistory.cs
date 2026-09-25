namespace RuAgent.Runtime.Worker;

/// <summary>
/// Per-connection request history: duplicate-request detection plus the monotonic
/// per-request frame sequence. Entries are evicted oldest-first past a fixed window
/// so a long-lived connection cannot grow this state without bound.
/// </summary>
public sealed class WorkerRequestHistory
{
    public const int DefaultLimit = 2048;

    private readonly object _gate = new();
    private readonly int _limit;
    private readonly HashSet<string> _begun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public WorkerRequestHistory(int limit = DefaultLimit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "History limit must be positive.");
        }

        _limit = limit;
    }

    /// <summary>
    /// Records the first start of a request id. Returns false for a duplicate id
    /// inside the retained window.
    /// </summary>
    public bool TryBegin(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (!_begun.Add(id))
            {
                return false;
            }

            Touch(id);
            return true;
        }
    }

    /// <summary>
    /// Returns the next frame sequence for a request id, starting at 1. Assigns
    /// sequences even for ids that never began, so late error frames for an
    /// unknown id stay monotonic for the life of the window.
    /// </summary>
    public long NextSequence(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            var next = _sequences.TryGetValue(id, out var value) ? value : 1;
            _sequences[id] = checked(next + 1);
            Touch(id);
            return next;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    private void Touch(string id)
    {
        if (!_tracked.Add(id))
        {
            return;
        }

        _order.Enqueue(id);
        while (_order.Count > _limit)
        {
            var oldest = _order.Dequeue();
            _tracked.Remove(oldest);
            _sequences.Remove(oldest);
            _begun.Remove(oldest);
        }
    }
}
