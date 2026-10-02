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
        Interlocked.Increment(ref _openRequests);
        Touch();
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
