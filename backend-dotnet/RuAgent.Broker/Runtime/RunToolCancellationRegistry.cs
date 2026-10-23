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
    private readonly object _gate = new();
    private readonly Dictionary<RunBucketKey, Dictionary<long, CancellationTokenSource>> _byRun = new();
    private readonly Dictionary<string, BrokerRunLifetime> _lifetimesByRun = new(StringComparer.Ordinal);
    private long _nextRegistration;

    /// <summary>Associates cancellation with the current lease generation for a run.</summary>
    internal void BindRun(string runId, BrokerRunLifetime lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (_gate) _lifetimesByRun[runId] = lifetime;
    }

    /// <summary>Releases the generation binding after its lease is closed.</summary>
    internal void UnbindRun(string runId, BrokerRunLifetime lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (_gate)
        {
            if (_lifetimesByRun.TryGetValue(runId, out var current) && ReferenceEquals(current, lifetime))
                _lifetimesByRun.Remove(runId);
        }
    }

    internal void UnbindRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;
        lock (_gate) _lifetimesByRun.Remove(runId);
    }

    /// <summary>
    /// Links a new execution to <paramref name="requestToken"/> under
    /// <paramref name="runId"/>. Dispose the result when the execution ends so
    /// the registry does not accumulate dead entries.
    /// </summary>
    public Registration Register(string runId, CancellationToken requestToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate)
        {
            var lifetime = _lifetimesByRun.GetValueOrDefault(runId);
            var key = new RunBucketKey(runId, lifetime);
            var source = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(requestToken)
                : CancellationTokenSource.CreateLinkedTokenSource(requestToken, lifetime.RevocationToken);
            var id = Interlocked.Increment(ref _nextRegistration);
            if (!_byRun.TryGetValue(key, out var bucket))
                _byRun.Add(key, bucket = new Dictionary<long, CancellationTokenSource>());
            bucket.Add(id, source);
            return new Registration(this, key, id, source);
        }
    }

    /// <summary>
    /// Links an execution to both its request and lease generation. Revocation
    /// remains sticky on the lifetime, so a late registration cannot escape a
    /// run cancel even when the run's active registration bucket was removed.
    /// </summary>
    internal Registration Register(string runId, BrokerRunLifetime lifetime, CancellationToken requestToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (_gate)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(requestToken, lifetime.RevocationToken);
            var key = new RunBucketKey(runId, lifetime);
            var id = Interlocked.Increment(ref _nextRegistration);
            if (!_byRun.TryGetValue(key, out var bucket))
                _byRun.Add(key, bucket = new Dictionary<long, CancellationTokenSource>());
            bucket.Add(id, source);
            return new Registration(this, key, id, source);
        }
    }

    /// <summary>
    /// Cancels all work currently registered for a run without revoking its
    /// lease. Used when session grants change: subsequent work must be able to
    /// re-evaluate under the new grants.
    /// </summary>
    internal int CancelActiveRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return 0;
        List<CancellationTokenSource> sources;
        lock (_gate)
        {
            var lifetime = _lifetimesByRun.GetValueOrDefault(runId);
            sources = RemoveBucketsUnderLock(key => key.RunId == runId
                && (key.Lifetime is null || ReferenceEquals(key.Lifetime, lifetime)));
        }

        return CancelSources(sources);
    }

    /// <summary>
    /// Cancels every execution registered for the run and permanently revokes
    /// its currently bound lease generation. Returns how many were signalled.
    /// A run with nothing in flight is a normal no-op, not an error.
    /// </summary>
    public int CancelRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return 0;
        BrokerRunLifetime? lifetime;
        List<CancellationTokenSource> sources;
        lock (_gate)
        {
            lifetime = _lifetimesByRun.GetValueOrDefault(runId);
            sources = RemoveBucketsUnderLock(key => key.RunId == runId);
        }

        // Revoke and cancel outside the lock because either operation may run
        // arbitrary token callbacks, including callbacks that register work.
        lifetime?.Revoke();
        return CancelSources(sources);
    }

    private List<CancellationTokenSource> RemoveBucketsUnderLock(Func<RunBucketKey, bool> predicate)
    {
        var sources = new List<CancellationTokenSource>();
        foreach (var key in _byRun.Keys.Where(predicate).ToArray())
        {
            var bucket = _byRun[key];
            sources.AddRange(bucket.Values);
            _byRun.Remove(key);
        }
        return sources;
    }

    private static int CancelSources(IEnumerable<CancellationTokenSource> sources)
    {
        var signalled = 0;
        foreach (var source in sources)
        {
            // The registration owns and disposes the source, so cancellation
            // must not dispose it out from under a runner unwinding its process tree.
            try
            {
                source.Cancel();
                signalled++;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
                // The source is signalled even when a callback fails; cancel peers too.
            }
        }
        return signalled;
    }

    public void CancelAll()
    {
        BrokerRunLifetime[] lifetimes;
        List<CancellationTokenSource> sources;
        lock (_gate)
        {
            lifetimes = _lifetimesByRun.Values.Distinct().ToArray();
            sources = RemoveBucketsUnderLock(static _ => true);
        }

        foreach (var lifetime in lifetimes) lifetime.Revoke();
        CancelSources(sources);
        lock (_gate)
        {
            foreach (var pair in _lifetimesByRun.ToArray())
            {
                if (lifetimes.Contains(pair.Value)) _lifetimesByRun.Remove(pair.Key);
            }
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            if (_byRun.TryGetValue(registration.BucketKey, out var bucket))
            {
                bucket.Remove(registration.RegistrationId);
                if (bucket.Count == 0) _byRun.Remove(registration.BucketKey);
            }
        }
        registration.Source.Dispose();
    }

    public sealed class Registration : IDisposable
    {
        private readonly RunToolCancellationRegistry _owner;
        private int _disposed;

        internal Registration(RunToolCancellationRegistry owner, RunBucketKey bucketKey, long id, CancellationTokenSource source)
        {
            _owner = owner;
            BucketKey = bucketKey;
            RegistrationId = id;
            Source = source;
        }

        internal RunBucketKey BucketKey { get; }
        internal long RegistrationId { get; }
        internal CancellationTokenSource Source { get; }

        /// <summary>The token that a cancel signals. Cancelling it ends the run's live work.</summary>
        public CancellationToken Token => Source.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Remove(this);
        }
    }

    internal readonly record struct RunBucketKey(string RunId, BrokerRunLifetime? Lifetime);
}
