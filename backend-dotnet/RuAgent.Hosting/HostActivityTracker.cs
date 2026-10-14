namespace RuAgent.Hosting;

/// <summary>
/// HTTP activity for the managed host's idle-shutdown policy: the last request time and
/// the number of requests currently in flight (a streaming response counts as in flight).
/// </summary>
public sealed class HostActivityTracker
{
    private readonly TimeProvider _clock;
    private long _lastActivityUtcTicks;
    private int _openRequests;
    private readonly object _admissionGate = new();
    private bool _maintenance;

    public HostActivityTracker(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastActivityUtcTicks = _clock.GetUtcNow().UtcTicks;
    }

    /// <summary>Requests being served right now, including open event streams.</summary>
    public int OpenRequests => Volatile.Read(ref _openRequests);

    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    public void Enter()
    {
        if (!TryEnter()) throw new InvalidOperationException("host_update_in_progress");
    }

    public bool TryEnter()
    {
        lock (_admissionGate)
        {
            if (_maintenance) return false;
            Interlocked.Increment(ref _openRequests);
            Touch();
            return true;
        }
    }

    // The one admitted request is the authenticated maintenance caller.
    // While it checks durable runs, no new request can create a run behind it.
    public bool TryBeginMaintenance()
    {
        lock (_admissionGate)
        {
            if (_maintenance || OpenRequests != 1) return false;
            _maintenance = true;
            return true;
        }
    }

    public void CancelMaintenance()
    {
        lock (_admissionGate) _maintenance = false;
    }

    public void Exit() => Interlocked.Decrement(ref _openRequests);

    public void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, _clock.GetUtcNow().UtcTicks);

    /// <summary>
    /// Idle when a window is configured, nothing is in flight, no run is registered and
    /// nothing was served for the whole window. A zero or negative window disables it.
    /// </summary>
    public static bool ShouldShutdown(
        DateTimeOffset now,
        DateTimeOffset lastActivity,
        int openRequests,
        int activeWork,
        TimeSpan window) =>
        window > TimeSpan.Zero
        && openRequests == 0
        && activeWork == 0
        && now - lastActivity >= window;
}
