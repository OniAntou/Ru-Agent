namespace RuApp.Services;

internal sealed class ChatTurnQueue
{
    public const int DefaultCapacity = 8;

    private readonly int _capacity;
    private readonly Queue<ChatTurn> _pending = new();
    private readonly object _sync = new();
    private ChatTurn? _active;
    private int _generation;

    public ChatTurnQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return (_active is null ? 0 : 1) + _pending.Count;
            }
        }
    }

    public int Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public ChatTurn? ActiveTurn
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public bool TryEnqueue(string sessionId, string correlationId, out ChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("A correlation id is required.", nameof(correlationId));
        }

        lock (_sync)
        {
            if ((_active is null ? 0 : 1) + _pending.Count >= _capacity)
            {
                turn = default!;
                return false;
            }

            turn = new ChatTurn(
                $"turn_{Guid.NewGuid():N}",
                sessionId,
                correlationId,
                _generation);
            if (_active is null)
            {
                _active = turn;
            }
            else
            {
                _pending.Enqueue(turn);
            }

            return true;
        }
    }

    public bool TryGetActive(string correlationId, out ChatTurn turn)
    {
        lock (_sync)
        {
            if (_active is not null
                && string.Equals(_active.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                turn = _active;
                return true;
            }

            turn = default!;
            return false;
        }
    }

    public bool Complete(string correlationId)
    {
        lock (_sync)
        {
            if (_active is null
                || !string.Equals(_active.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                return false;
            }

            _active = _pending.Count > 0 ? _pending.Dequeue() : null;
            return true;
        }
    }

    public bool Remove(string correlationId)
    {
        lock (_sync)
        {
            if (_active is not null
                && string.Equals(_active.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                _active = _pending.Count > 0 ? _pending.Dequeue() : null;
                return true;
            }

            if (_pending.Count == 0)
            {
                return false;
            }

            var kept = new Queue<ChatTurn>(_pending.Count);
            var removed = false;
            while (_pending.Count > 0)
            {
                var candidate = _pending.Dequeue();
                if (!removed
                    && string.Equals(candidate.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                kept.Enqueue(candidate);
            }

            while (kept.Count > 0)
            {
                _pending.Enqueue(kept.Dequeue());
            }

            return removed;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            _active = null;
            _pending.Clear();
        }
    }
}
