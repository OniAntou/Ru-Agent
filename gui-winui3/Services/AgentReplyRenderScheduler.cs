using System;

namespace RuApp.Services;

public sealed class AgentReplyRenderScheduler
{
    private readonly AgentReplyAccumulator _accumulator;
    private readonly Action<Action> _dispatch;
    private readonly Action<AgentReplySnapshot> _apply;
    private readonly object _gate = new();
    private long _generation;
    private bool _callbackPending;
    private bool _pendingWork;

    public AgentReplyRenderScheduler(
        AgentReplyAccumulator accumulator,
        Action<Action> dispatch,
        Action<AgentReplySnapshot> apply)
    {
        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public void Schedule()
    {
        long generation;
        lock (_gate)
        {
            _pendingWork = true;
            if (_callbackPending)
            {
                return;
            }

            _callbackPending = true;
            generation = _generation;
        }

        Enqueue(generation);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _pendingWork = false;
        }

        _accumulator.Invalidate();
    }

    public AgentReplySnapshot? Flush()
    {
        AgentReplySnapshot? snapshot;
        lock (_gate)
        {
            _generation++;
            _pendingWork = false;
            snapshot = _accumulator.TakeSnapshot();
        }

        _accumulator.Invalidate();
        return snapshot;
    }

    /// <summary>
    /// How often a rejected dispatch is retried. One retry covers a transient
    /// rejection (a dispatch that races teardown); an unbounded retry would spin
    /// forever when the dispatcher is already closed.
    /// </summary>
    private const int MaxDispatchRetries = 1;

    private void Enqueue(long generation) => Enqueue(generation, attempts: 0);

    private void Enqueue(long generation, int attempts)
    {
        try
        {
            _dispatch(() => Run(generation));
        }
        catch (OperationCanceledException)
        {
            bool retryForCurrentGeneration;
            long currentGeneration;
            lock (_gate)
            {
                // The callback was rejected, so it cannot clear ownership later.
                _callbackPending = false;
                currentGeneration = _generation;
                retryForCurrentGeneration = _pendingWork && attempts < MaxDispatchRetries;
                if (retryForCurrentGeneration)
                {
                    _callbackPending = true;
                }
            }

            if (retryForCurrentGeneration)
            {
                Enqueue(currentGeneration, attempts + 1);
            }
        }
        catch
        {
            lock (_gate)
            {
                _callbackPending = false;
            }

            throw;
        }
    }

    private void Run(long callbackGeneration)
    {
        AgentReplySnapshot? snapshot = null;
        long? replacementGeneration = null;
        lock (_gate)
        {
            if (!_callbackPending)
            {
                return;
            }

            _callbackPending = false;
            if (callbackGeneration == _generation)
            {
                snapshot = _accumulator.TakeSnapshot();
                _pendingWork = false;
            }
            else if (_pendingWork)
            {
                _callbackPending = true;
                replacementGeneration = _generation;
            }
        }

        if (snapshot != null)
        {
            _apply(snapshot);
        }

        if (replacementGeneration is long nextGeneration)
        {
            Enqueue(nextGeneration);
        }
    }
}
