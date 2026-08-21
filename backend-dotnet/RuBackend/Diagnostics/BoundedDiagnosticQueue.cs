namespace RuBackend.Diagnostics;

public sealed class BoundedDiagnosticQueue
{
    private readonly int _capacity;
    private readonly LinkedList<DiagnosticEvent> _items = new();
    private readonly object _lock = new();
    private long _enqueuedCount;
    private long _droppedCount;
    private long _writeFailureCount;

    public BoundedDiagnosticQueue(int capacity = 1000)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Capacity => _capacity;

    public bool TryEnqueue(DiagnosticEvent evt)
    {
        lock (_lock)
        {
            if (_items.Count >= _capacity)
            {
                // Check if candidate has higher severity than something in the queue
                var candidateSeverity = GetSeverityRank(evt.Level);
                LinkedListNode<DiagnosticEvent>? lowestNode = null;
                var lowestRank = candidateSeverity;

                for (var node = _items.First; node != null; node = node.Next)
                {
                    var rank = GetSeverityRank(node.Value.Level);
                    if (rank < lowestRank)
                    {
                        lowestRank = rank;
                        lowestNode = node;
                    }
                }

                if (lowestNode != null)
                {
                    _items.Remove(lowestNode);
                    _droppedCount++;
                }
                else
                {
                    // Candidate is lowest or equal, drop candidate
                    _droppedCount++;
                    return false;
                }
            }

            _items.AddLast(evt);
            _enqueuedCount++;
            return true;
        }
    }

    public int DrainBatch(List<DiagnosticEvent> target, int maxBatchSize)
    {
        lock (_lock)
        {
            var count = 0;
            while (_items.Count > 0 && count < maxBatchSize)
            {
                target.Add(_items.First!.Value);
                _items.RemoveFirst();
                count++;
            }
            return count;
        }
    }

    public void RequeueFront(IReadOnlyList<DiagnosticEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            for (var index = events.Count - 1; index >= 0; index--)
            {
                if (_items.Count >= _capacity)
                {
                    _items.RemoveFirst();
                    _droppedCount++;
                }

                _items.AddFirst(events[index]);
            }
        }
    }

    public void RecordWriteFailure()
    {
        Interlocked.Increment(ref _writeFailureCount);
    }

    public DiagnosticStatusSnapshot GetSnapshot(long totalBytes = 0, int activeFilesCount = 0)
    {
        lock (_lock)
        {
            return new DiagnosticStatusSnapshot(
                QueueDepth: _items.Count,
                EnqueuedCount: _enqueuedCount,
                DroppedCount: _droppedCount,
                WriteFailureCount: Interlocked.Read(ref _writeFailureCount),
                TotalBytes: totalBytes,
                ActiveFilesCount: activeFilesCount);
        }
    }

    private static int GetSeverityRank(string level) => level?.ToLowerInvariant() switch
    {
        "trace" => 1,
        "debug" => 2,
        "info" => 3,
        "warning" => 4,
        "error" => 5,
        "critical" => 6,
        _ => 3,
    };
}
