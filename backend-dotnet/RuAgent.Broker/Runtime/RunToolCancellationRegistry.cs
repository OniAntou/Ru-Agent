using System.Collections.Concurrent;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// Tracks the tool executions currently in flight for each open run lease.
/// A cancel or a run close terminates the linked token, and the terminal
/// adapter turns that into a Job Object kill of the whole process tree. Without
/// this, the Host can commit a durable cancelled projection while the child
/// process keeps mutating the workspace.
/// </summary>
public sealed class RunToolCancellationRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, CancellationTokenSource>> _byRun =
        new(StringComparer.Ordinal);
    private long _nextRegistration;

    /// <summary>
    /// Links a new execution to <paramref name="requestToken"/> under
    /// <paramref name="runId"/>. Dispose the result when the execution ends so
    /// the registry does not accumulate dead entries.
    /// </summary>
    public Registration Register(string runId, CancellationToken requestToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        var registrationId = Interlocked.Increment(ref _nextRegistration);
        var bucket = _byRun.GetOrAdd(
            runId,
            static _ => new ConcurrentDictionary<long, CancellationTokenSource>());
        bucket[registrationId] = source;
        return new Registration(this, runId, registrationId, source);
    }

    /// <summary>
    /// Cancels every execution registered for the run. Returns how many were
    /// signalled; a run with nothing in flight is a normal no-op, not an error.
    /// </summary>
    public int CancelRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || !_byRun.TryRemove(runId, out var bucket))
        {
            return 0;
        }

        var signalled = 0;
        foreach (var entry in bucket)
        {
            // The registration keeps ownership of the source and disposes it,
            // so cancelling here must not dispose it out from under a runner
            // that is still unwinding its process tree.
            try
            {
                entry.Value.Cancel();
                signalled++;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        bucket.Clear();
        return signalled;
    }

    public sealed class Registration : IDisposable
    {
        private readonly RunToolCancellationRegistry _owner;
        private readonly string _runId;
        private readonly long _registrationId;
        private readonly CancellationTokenSource _source;
        private int _disposed;

        public Registration(
            RunToolCancellationRegistry owner,
            string runId,
            long registrationId,
            CancellationTokenSource source)
        {
            _owner = owner;
            _runId = runId;
            _registrationId = registrationId;
            _source = source;
        }


        /// <summary>The token that a cancel signals. Cancelling it ends the run's live work.</summary>
        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_owner._byRun.TryGetValue(_runId, out var bucket))
            {
                bucket.TryRemove(_registrationId, out _);
            }

            _source.Dispose();
        }
    }
}
