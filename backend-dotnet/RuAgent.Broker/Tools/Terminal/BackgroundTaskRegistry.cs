using System.Text;
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
    string Text);

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

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, TaskEntry>> _sessions = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>
    /// Registers one running task. False when the session already keeps the
    /// maximum number of running tasks, or the id is still running.
    /// </summary>
    public bool TryStart(string sessionId, string executionId, int lifetimeMs, out CancellationToken lifetimeToken)
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

            if (running >= TerminalLimits.MaxBackgroundTasksPerSession)
            {
                lifetimeToken = default;
                return false;
            }

            var lifetime = new CancellationTokenSource(Math.Max(1, lifetimeMs));
            tasks[executionId] = new TaskEntry(executionId, lifetime, ++_sequence);
            EvictCompleted(tasks);
            lifetimeToken = lifetime.Token;
            return true;
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
            if (room > 0)
            {
                entry.Text.Append(frame, 0, Math.Min(frame.Length, room));
            }
        }
    }

    /// <summary>
    /// Finalizes the task: the state comes from the user kill flag, the
    /// lifetime token, then the runner's result status.
    /// </summary>
    public void Complete(string sessionId, string executionId, string resultStatus, int? exitCode, string finalText)
    {
        lock (_gate)
        {
            if (!TryFind(sessionId, executionId, out var entry))
            {
                return;
            }

            entry.State = entry.Killed
                ? BackgroundTaskState.Killed
                : entry.Lifetime.IsCancellationRequested
                    ? BackgroundTaskState.Expired
                    : resultStatus switch
                    {
                        ToolResultStatuses.Succeeded => BackgroundTaskState.Exited,
                        ToolResultStatuses.TimedOut => BackgroundTaskState.Expired,
                        ToolResultStatuses.Cancelled => BackgroundTaskState.Killed,
                        _ => BackgroundTaskState.Failed,
                    };
            entry.ExitCode = exitCode;
            entry.Text.Clear();
            var retained = finalText.Length > TerminalLimits.MaxSpoolChars
                ? finalText[..TerminalLimits.MaxSpoolChars]
                : finalText;
            entry.Text.Append(retained);
            entry.Lifetime.Dispose();
        }
    }

    public bool TryRead(string sessionId, string executionId, out BackgroundTaskSnapshot snapshot)
    {
        lock (_gate)
        {
            if (TryFind(sessionId, executionId, out var entry))
            {
                snapshot = new BackgroundTaskSnapshot(
                    entry.ExecutionId,
                    entry.State,
                    entry.ExitCode,
                    entry.Text.ToString());
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    /// <summary>Cancels a running task's lifetime token so the runner kills its process tree.</summary>
    public BackgroundKillOutcome Kill(string sessionId, string executionId)
    {
        lock (_gate)
        {
            if (!TryFind(sessionId, executionId, out var entry))
            {
                return BackgroundKillOutcome.Unknown;
            }

            if (entry.State != BackgroundTaskState.Running)
            {
                return BackgroundKillOutcome.NotRunning;
            }

            entry.Killed = true;
            try
            {
                entry.Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The task completed between the state check and the cancel.
            }

            return BackgroundKillOutcome.Killing;
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

        public long Sequence { get; }
    }
}
