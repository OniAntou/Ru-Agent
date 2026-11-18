using System.Collections.Concurrent;

namespace RuBackend.Harness;

/// <summary>
/// Process-local retry spacing for a registered run whose drive keeps failing in a way the
/// loop cannot record durably (the store refused even the failure transition). Such a run
/// stays registered for a later retry; without spacing the loop retried it every poll and
/// logged a warning and an error each time. A first failure retries on the next pass, as
/// before (a transient store error usually clears at once); from the second consecutive
/// failure the delay doubles from the poll interval up to <see cref="MaximumDelay"/>. The
/// backoff clears on the first pass that completes without a failure.
/// </summary>
internal sealed class AgentRunDriveBackoff
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);
    private const int MaximumDoublings = 16;

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset RetryAt)> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _initialDelay;
    private readonly Func<DateTimeOffset> _now;

    internal AgentRunDriveBackoff(TimeSpan initialDelay, Func<DateTimeOffset>? now = null)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        _initialDelay = initialDelay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when the run has no pending backoff or its retry time has come.</summary>
    internal bool IsDue(string runId) =>
        !_entries.TryGetValue(runId, out var entry) || _now() >= entry.RetryAt;

    /// <summary>The consecutive failures recorded for the run; a pass compares it before and after.</summary>
    internal int Failures(string runId) => _entries.TryGetValue(runId, out var entry) ? entry.Failures : 0;

    internal TimeSpan RecordFailure(string runId)
    {
        var entry = _entries.AddOrUpdate(
            runId,
            _ => (1, _now() + DelayFor(1)),
            (_, current) => (current.Failures + 1, _now() + DelayFor(current.Failures + 1)));
        return DelayFor(entry.Failures);
    }

    /// <summary>Clears the run's backoff unless a failure was recorded since <paramref name="failuresBefore"/>.</summary>
    internal void RecordPass(string runId, int failuresBefore)
    {
        if (_entries.TryGetValue(runId, out var entry) && entry.Failures == failuresBefore)
        {
            _entries.TryRemove(new KeyValuePair<string, (int, DateTimeOffset)>(runId, entry));
        }
    }

    internal void Forget(string runId) => _entries.TryRemove(runId, out _);

    private TimeSpan DelayFor(int failures)
    {
        if (failures <= 1)
        {
            return TimeSpan.Zero;
        }

        var doublings = Math.Min(failures - 2, MaximumDoublings);
        var ticks = _initialDelay.Ticks * (1L << doublings);
        return ticks <= 0 || ticks > MaximumDelay.Ticks ? MaximumDelay : TimeSpan.FromTicks(ticks);
    }
}
