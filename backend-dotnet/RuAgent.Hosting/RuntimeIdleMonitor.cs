namespace RuAgent.Hosting;

public sealed class RuntimeIdleMonitor
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _idleTimeout;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly Func<bool> _hasActiveRun;
    private readonly Func<bool> _hasPendingApproval;
    private readonly RuntimeClientLeaseRegistry _leaseRegistry;
    private DateTimeOffset _lastActivity;

    public RuntimeIdleMonitor(
        RuntimeClientLeaseRegistry leaseRegistry,
        Func<DateTimeOffset>? nowProvider = null,
        TimeSpan? idleTimeout = null,
        Func<bool>? hasActiveRun = null,
        Func<bool>? hasPendingApproval = null)
    {
        _leaseRegistry = leaseRegistry ?? throw new ArgumentNullException(nameof(leaseRegistry));
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (_idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        _hasActiveRun = hasActiveRun ?? (() => false);
        _hasPendingApproval = hasPendingApproval ?? (() => false);
        _lastActivity = _nowProvider();
    }

    public TimeSpan IdleTimeout => _idleTimeout;

    public void NotifyActivity()
    {
        _lastActivity = _nowProvider();
    }

    public void NotifyActivity(DateTimeOffset now)
    {
        _lastActivity = now;
    }

    public bool ShouldShutdown()
    {
        return ShouldShutdown(_nowProvider());
    }

    public bool ShouldShutdown(DateTimeOffset now)
    {
        // Suppressed if any condition holds
        if (_hasActiveRun()) return false;
        if (_hasPendingApproval()) return false;
        if (_leaseRegistry.HasLeases) return false;

        var idle = now - _lastActivity;
        return idle >= _idleTimeout;
    }

    public DateTimeOffset LastActivity => _lastActivity;
}
