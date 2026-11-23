using System.Diagnostics;
using System.Text;
using System.Threading;
using RuAgent.Broker.Configuration;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>Lifecycle of one background terminal task.</summary>
public enum BackgroundTaskState
{
    Running,
    Exited,
    Killed,
    Expired,
    Failed,
}

/// <summary>Immutable view of one background task for the read/kill legs.</summary>
public sealed record BackgroundTaskSnapshot(
    string ExecutionId,
    BackgroundTaskState State,
    int? ExitCode,
    string Text,
    bool IsBackground,
    string Command = "",
    bool TextTruncated = false);

/// <summary>
/// One wait result: the task view after the wait and whether output or state
/// moved. An unchanged result means the deadline elapsed with no new output, or
/// the task had already stopped before the wait began.
/// </summary>
public sealed record BackgroundTaskWaitResult(
    BackgroundTaskSnapshot Snapshot,
    long Revision,
    bool Changed);

public enum BackgroundKillOutcome
{
    Killing,
    NotRunning,
    Unknown,
}

/// <summary>
/// Session-scoped registry of background terminal tasks. The Broker owns the
/// process and its lifetime token: mode read_output pages live text while the
/// task runs, mode kill stops it, and a task that reaches its lifetime cap is
/// killed by the same token. Entries die with the Broker process, so a stale
/// id fails closed instead of reaching another session's task.
/// </summary>
public sealed class BackgroundTaskRegistry
{
    /// <summary>Completed entries kept per session for state and exit reads.</summary>
    public const int MaxCompletedEntriesPerSession = 16;

    private readonly int _maxRunningTasksPerSession;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, TaskEntry>> _sessions = new(StringComparer.Ordinal);
    private long _sequence;

    public BackgroundTaskRegistry(int maxRunningTasksPerSession = TerminalLimits.MaxBackgroundTasksPerSession)
    {
        if (maxRunningTasksPerSession is < TerminalConcurrencyOptions.MinimumMaxConcurrentTasks
            or > TerminalConcurrencyOptions.MaximumMaxConcurrentTasks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRunningTasksPerSession),
                $"The running task capacity must be from {TerminalConcurrencyOptions.MinimumMaxConcurrentTasks} to {TerminalConcurrencyOptions.MaximumMaxConcurrentTasks}.");
        }

        _maxRunningTasksPerSession = maxRunningTasksPerSession;
    }

    /// <summary>Cancels running task tokens owned by one session, retaining their entries.</summary>
    public void CancelSession(string sessionId)
    {
        List<TaskEntry> toCancel;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var tasks))
            {
                return;
            }

            toCancel = tasks.Values
                .Where(entry => entry.State == BackgroundTaskState.Running)
                .ToList();
            foreach (var entry in toCancel)
            {
                entry.CancellationRequested = true;
            }
        }

        CancelLifetimes(toCancel);
    }

    /// <summary>Cancels running tasks and removes all retained state for one session.</summary>
    public void RemoveSession(string sessionId)
    {
        List<TaskEntry> removed;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out var tasks))
            {
                return;
            }

            removed = tasks.Values.ToList();
            foreach (var entry in removed)
            {
                if (entry.State == BackgroundTaskState.Running)
                {
                    entry.CancellationRequested = true;
                }
            }
        }

        CancelLifetimes(removed.Where(entry => entry.CancellationRequested));
        foreach (var entry in removed)
        {
            entry.DisposeLifetime();
        }
    }

    /// <summary>Cancels running task tokens across all sessions during service shutdown.</summary>
    public void CancelAll()
    {
        List<TaskEntry> toCancel;
        lock (_gate)
        {
            toCancel = _sessions.Values
                .SelectMany(tasks => tasks.Values)
                .Where(entry => entry.State == BackgroundTaskState.Running)
                .ToList();
            foreach (var entry in toCancel)
            {
                entry.CancellationRequested = true;
            }
        }

        CancelLifetimes(toCancel);
    }

    private static void CancelLifetimes(IEnumerable<TaskEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.CancelLifetime();
        }
    }

    /// <summary>
    /// Registers one running task. False when the session already keeps the
    /// maximum number of running tasks or the id is still running. Capacity is
    /// reserved for every launch that may later move to the background.
    /// </summary>
    public bool TryStart(
        string sessionId,
        string executionId,
        int lifetimeMs,
        bool isBackground,
        out CancellationToken lifetimeToken,
        string displayCommand = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var tasks))
            {
                tasks = new Dictionary<string, TaskEntry>(StringComparer.Ordinal);
                _sessions[sessionId] = tasks;
            }

            if (tasks.TryGetValue(executionId, out var existing))
            {
                if (existing.State == BackgroundTaskState.Running)
                {
                    lifetimeToken = default;
                    return false;
                }

                tasks.Remove(executionId);
            }

            var running = 0;
            foreach (var entry in tasks.Values)
            {
                if (entry.State == BackgroundTaskState.Running)
                {
                    running++;
                }
            }

            if (running >= _maxRunningTasksPerSession)
            {
                lifetimeToken = default;
                return false;
            }

            var lifetime = new CancellationTokenSource(Math.Max(1, lifetimeMs));
            tasks[executionId] = new TaskEntry(executionId, lifetime, ++_sequence)
            {
                IsBackground = isBackground,
                Command = displayCommand,
            };
            EvictCompleted(tasks);
            lifetimeToken = lifetime.Token;
            return true;
        }
    }

    /// <summary>Marks a foreground task as background after its wait window expired.</summary>
    public void MarkBackground(string sessionId, string executionId)
    {
        lock (_gate)
        {
            if (TryFind(sessionId, executionId, out var entry))
            {
                entry.IsBackground = true;
            }
        }
    }

    /// <summary>Appends one streamed frame to the live text, bounded by the spool cap.</summary>
    public void Append(string sessionId, string executionId, string frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            return;
        }

        lock (_gate)
        {
            if (!TryFind(sessionId, executionId, out var entry) || entry.State != BackgroundTaskState.Running)
            {
                return;
            }

            var room = TerminalLimits.MaxSpoolChars - entry.Text.Length;
            var appended = Math.Min(frame.Length, Math.Max(0, room));
            if (appended > 0)
            {
                entry.Text.Append(frame, 0, appended);
            }

            var truncated = frame.Length > room;
            if (truncated && !entry.TextTruncated)
            {
                // The live window is the head only; say so instead of letting a
                // later read treat it as the complete capture.
                entry.TextTruncated = true;
            }

            if (appended > 0 || truncated)
            {
                entry.MarkChanged();
            }
        }
    }

    /// <summary>
    /// Finalizes the task: the state comes from the user kill flag, the
    /// lifetime token, then the runner's result status.
    /// </summary>
    /// <param name="textTruncated">
    /// True when the task produced output beyond the retained live window, so a
    /// read after the spool is gone still reports an incomplete capture.
    /// </param>
    public void Complete(
        string sessionId,
        string executionId,
        string resultStatus,
        int? exitCode,
        string finalText,
        bool textTruncated = false)
    {
        TaskEntry completed;
        lock (_gate)
        {
            if (!TryFind(sessionId, executionId, out var entry))
            {
                return;
            }

            entry.State = entry.Killed
                ? BackgroundTaskState.Killed
                : entry.CancellationRequested || entry.Lifetime.IsCancellationRequested
                    ? BackgroundTaskState.Expired
                    : resultStatus switch
                    {
                        ToolResultStatuses.Succeeded => BackgroundTaskState.Exited,
                        ToolResultStatuses.TimedOut => BackgroundTaskState.Expired,
                        ToolResultStatuses.Cancelled => BackgroundTaskState.Killed,
                        _ => BackgroundTaskState.Failed,
                    };
            entry.ExitCode = exitCode;
            entry.TextTruncated |= textTruncated;
            entry.Text.Clear();
            var retained = finalText.Length > TerminalLimits.MaxSpoolChars
                ? finalText[..TerminalLimits.MaxSpoolChars]
                : finalText;
            entry.Text.Append(retained);
            entry.MarkChanged();
            completed = entry;
        }

        completed.DisposeLifetime();
    }

    public bool TryRead(string sessionId, string executionId, out BackgroundTaskSnapshot snapshot)
    {
        lock (_gate)
        {
            if (TryFind(sessionId, executionId, out var entry))
            {
                snapshot = SnapshotOf(entry);
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    /// <summary>Current revision of one tracked task, or false when the id is unknown.</summary>
    public bool TryGetRevision(string sessionId, string executionId, out long revision)
    {
        lock (_gate)
        {
            if (TryFind(sessionId, executionId, out var entry))
            {
                revision = entry.Revision;
                return true;
            }
        }

        revision = 0;
        return false;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a running task to move past
    /// <paramref name="revision"/>, then reports its view. A task that already
    /// stopped answers at once without a change. The unchanged result means the
    /// deadline elapsed with no new output; null means the id is unknown to this
    /// session, so the caller fails closed instead of waiting on another
    /// session's task.
    /// </summary>
    public async Task<BackgroundTaskWaitResult?> WaitForRevisionAsync(
        string sessionId,
        string executionId,
        long revision,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (!TryFind(sessionId, executionId, out var entry))
                {
                    return null;
                }

                if (entry.Revision != revision)
                {
                    return new BackgroundTaskWaitResult(SnapshotOf(entry), entry.Revision, Changed: true);
                }

                // A task that already stopped cannot change again: answer at
                // once so the caller reads its final state instead of waiting.
                if (entry.State != BackgroundTaskState.Running)
                {
                    return new BackgroundTaskWaitResult(SnapshotOf(entry), entry.Revision, Changed: false);
                }

                changed = entry.Changed;
            }

            var remaining = TimeSpan.FromMilliseconds(timeoutMs) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return ObservedWithoutChange(sessionId, executionId);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(remaining);
            try
            {
                await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ObservedWithoutChange(sessionId, executionId);
            }
        }
    }

    private BackgroundTaskWaitResult? ObservedWithoutChange(string sessionId, string executionId)
    {
        lock (_gate)
        {
            return TryFind(sessionId, executionId, out var entry)
                ? new BackgroundTaskWaitResult(SnapshotOf(entry), entry.Revision, Changed: false)
                : null;
        }
    }

    private static BackgroundTaskSnapshot SnapshotOf(TaskEntry entry) =>
        new(
            entry.ExecutionId,
            entry.State,
            entry.ExitCode,
            entry.Text.ToString(),
            entry.IsBackground,
            entry.Command,
            entry.TextTruncated);

    /// <summary>Cancels a running task's lifetime token so the runner kills its process tree.</summary>
    public BackgroundKillOutcome Kill(string sessionId, string executionId)
    {
        TaskEntry entry;
        lock (_gate)
        {
            if (!TryFind(sessionId, executionId, out entry!))
            {
                return BackgroundKillOutcome.Unknown;
            }

            if (entry.State != BackgroundTaskState.Running)
            {
                return BackgroundKillOutcome.NotRunning;
            }

            entry.Killed = true;
            entry.CancellationRequested = true;
        }

        entry.CancelLifetime();
        return BackgroundKillOutcome.Killing;
    }

    /// <summary>Bounded snapshot of one session's tasks, oldest first.</summary>
    public IReadOnlyList<BackgroundTaskSnapshot> List(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var tasks))
            {
                return Array.Empty<BackgroundTaskSnapshot>();
            }

            var snapshots = new List<BackgroundTaskSnapshot>(tasks.Count);
            foreach (var entry in tasks.Values.OrderBy(entry => entry.Sequence))
            {
                snapshots.Add(SnapshotOf(entry));
            }

            return snapshots;
        }
    }

    private bool TryFind(string sessionId, string executionId, out TaskEntry entry)
    {
        if (_sessions.TryGetValue(sessionId, out var tasks) && tasks.TryGetValue(executionId, out var found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private static void EvictCompleted(Dictionary<string, TaskEntry> tasks)
    {
        var completed = new List<TaskEntry>();
        foreach (var entry in tasks.Values)
        {
            if (entry.State != BackgroundTaskState.Running)
            {
                completed.Add(entry);
            }
        }

        if (completed.Count <= MaxCompletedEntriesPerSession)
        {
            return;
        }

        completed.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        var excess = completed.Count - MaxCompletedEntriesPerSession;
        for (var index = 0; index < excess; index++)
        {
            tasks.Remove(completed[index].ExecutionId);
        }
    }

    private sealed class TaskEntry
    {
        public TaskEntry(string executionId, CancellationTokenSource lifetime, long sequence)
        {
            ExecutionId = executionId;
            Lifetime = lifetime;
            Sequence = sequence;
        }

        public string ExecutionId { get; }

        public CancellationTokenSource Lifetime { get; }

        public StringBuilder Text { get; } = new();

        public BackgroundTaskState State { get; set; } = BackgroundTaskState.Running;

        public int? ExitCode { get; set; }

        public bool Killed { get; set; }

        public bool CancellationRequested { get; set; }

        public bool IsBackground { get; set; }
        public string Command { get; init; } = string.Empty;

        /// <summary>True once output beyond the retained live window was observed.</summary>
        public bool TextTruncated { get; set; }

        public long Sequence { get; }

        /// <summary>
        /// Bumped under the registry lock whenever output or state changes, so a
        /// waiter can tell a real change from a spurious wake.
        /// </summary>
        public long Revision { get; private set; }

        /// <summary>
        /// Completes on the next change. Continuations run asynchronously so a
        /// waiter never re-enters the registry from the signalling thread.
        /// </summary>
        public Task Changed => _changed.Task;

        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Records one change and wakes every waiter. Caller holds the registry lock.</summary>
        public void MarkChanged()
        {
            Revision++;
            var signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }

        private int _lifetimeDisposed;

        public void CancelLifetime()
        {
            try
            {
                Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion or removal won the race; the token can no longer drive a task.
            }
            catch (AggregateException)
            {
                // Cancellation is already signaled. A faulty callback must not prevent peer tasks from being cancelled.
            }
        }

        public void DisposeLifetime()
        {
            if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
            {
                Lifetime.Dispose();
            }
        }
    }
}
