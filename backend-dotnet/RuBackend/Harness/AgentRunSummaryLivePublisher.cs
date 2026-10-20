using System.Collections.Concurrent;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

/// <summary>
/// Builds a bounded, safe terminal summary from runtime activity and optionally publishes
/// it as a transient SSE frame. Summary data is connection-only and is never persisted.
/// </summary>
public sealed class AgentRunSummaryLivePublisher : IAgentActivitySink
{
    private const int MaximumTerminalSummaries = 256;
    private const int MaximumActiveStates = 4096;

    private readonly HarnessLiveEventHub _liveEvents;
    private readonly AgentActivityLivePublisher _activityPublisher;
    private readonly ConcurrentDictionary<string, SummaryState> _states = new(StringComparer.Ordinal);
    private readonly SummaryState _discardedOverflowState = new();
    private readonly ConcurrentDictionary<string, byte> _reserved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRunSummary> _terminalSummaries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _terminalOrder = new();

    public AgentRunSummaryLivePublisher(HarnessLiveEventHub liveEvents, KnownSecretRedactor? secrets = null)
    {
        _liveEvents = liveEvents ?? throw new ArgumentNullException(nameof(liveEvents));
        _activityPublisher = new AgentActivityLivePublisher(liveEvents, secrets);
    }

    /// <summary>
    /// Records the safe activity and forwards it to the existing activity feed publisher.
    /// </summary>
    public void PublishActivity(RunId runId, AgentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // A terminal summary is an immutable snapshot. Ignore late activity rather
        // than recreating per-run state after the bounded cache has taken ownership.
        if (TryGetTerminalSummary(runId.Value, out _))
        {
            return;
        }

        var state = GetOrCreateState(runId.Value);
        if (TryGetTerminalSummary(runId.Value, out _))
        {
            _states.TryRemove(runId.Value, out _);
            return;
        }

        state.Apply(activity);
        if (TryGetTerminalSummary(runId.Value, out _))
        {
            _states.TryRemove(runId.Value, out _);
            return;
        }

        _activityPublisher.Publish(runId, activity);
    }

    public void Publish(RunId runId, AgentActivity activity) => PublishActivity(runId, activity);

    public void RegisterWorkspace(RunId runId, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        if (TryGetTerminalSummary(runId.Value, out _))
        {
            return;
        }

        var state = GetOrCreateState(runId.Value);
        if (TryGetTerminalSummary(runId.Value, out _))
        {
            _states.TryRemove(runId.Value, out _);
            return;
        }

        state.SetWorkspaceLabel(ToSafeWorkspaceLabel(workspaceRoot));
    }

    /// <summary>
    /// Reserves one terminal summary for a run. The endpoint uses this before writing a
    /// direct late-subscriber frame so a runtime publish cannot create a duplicate.
    /// </summary>
    public bool TryReserveSummary(AgentRun run, out AgentRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.State.IsTerminal())
        {
            summary = null!;
            return false;
        }

        if (TryGetTerminalSummary(run.RunId.Value, out summary))
        {
            return false;
        }

        summary = BuildSummary(run);
        if (!_reserved.TryAdd(run.RunId.Value, 0))
        {
            // Another caller won the reservation. Prefer its immutable snapshot when
            // available; the caller only needs to know that it must not publish again.
            if (TryGetTerminalSummary(run.RunId.Value, out var existing))
            {
                summary = existing;
            }

            return false;
        }

        CacheTerminalSummary(run.RunId.Value, summary);
        return true;
    }

    public AgentRunSummary BuildSummary(AgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (TryGetTerminalSummary(run.RunId.Value, out var cached))
        {
            return cached;
        }

        var state = GetOrCreateState(run.RunId.Value);
        var summary = AgentRunSummary.FromTerminalRun(
            run,
            state.WorkspaceLabel,
            state.ToMetrics());

        // Reservation can race this read. Returning the cached value when it won the
        // race keeps every late subscriber on the same terminal snapshot.
        return TryGetTerminalSummary(run.RunId.Value, out cached) ? cached : summary;
    }

    /// <summary>
    /// Publishes at most one summary through the live hub. A subscriber that connects
    /// after this frame was emitted can still use <see cref="BuildSummary"/> and write
    /// its own connection-local transient frame.
    /// </summary>
    public bool TryPublishSummary(AgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!TryReserveSummary(run, out var summary))
        {
            return false;
        }

        var wire = new AgentRunSummaryWire(
            summary.Status,
            summary.VerificationState,
            summary.WorkspaceLabel,
            summary.FilesScanned,
            summary.FilesChanged,
            summary.FilesCreated,
            summary.FilesDeleted);
        var data = AgentRunSummaryWireCodec.Serialize(wire);
        _liveEvents.TryPublish(run.RunId, "agent.summary", data);
        return true;
    }

    private bool TryGetTerminalSummary(string runId, out AgentRunSummary summary) =>
        _terminalSummaries.TryGetValue(runId, out summary!);

    /// <summary>
    /// Gets the live state for a run, or a shared discard state once too many runs
    /// are active at once. Hung runs never reach the terminal cache, so the live map
    /// must stay bounded even if that costs one pathological run its summary.
    /// </summary>
    private SummaryState GetOrCreateState(string runId)
    {
        if (_states.TryGetValue(runId, out var existing))
        {
            return existing;
        }

        if (_states.Count >= MaximumActiveStates)
        {
            return _discardedOverflowState;
        }

        return _states.GetOrAdd(runId, _ => new SummaryState());
    }

    private void CacheTerminalSummary(string runId, AgentRunSummary summary)
    {
        if (!_terminalSummaries.TryAdd(runId, summary))
        {
            return;
        }

        _terminalOrder.Enqueue(runId);
        _states.TryRemove(runId, out _);

        // Keep terminal snapshots bounded. The reservation set is evicted together
        // with the snapshot so this publisher cannot grow without limit over time.
        while (_terminalOrder.Count > MaximumTerminalSummaries
            && _terminalOrder.TryDequeue(out var evictedRunId))
        {
            _terminalSummaries.TryRemove(evictedRunId, out _);
            _reserved.TryRemove(evictedRunId, out _);
        }
    }

    private static string? ToSafeWorkspaceLabel(string workspaceRoot)
    {
        var normalized = workspaceRoot.Trim().TrimEnd('/', '\\');
        if (normalized.Length == 0)
        {
            return null;
        }

        var separator = normalized.LastIndexOfAny(['/', '\\']);
        var label = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        try
        {
            // Reuse the Core constructor's display-label validation without exposing the
            // original root to the public summary.
            _ = new AgentRunSummary("failed", "not_applicable", label);
            return label;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class SummaryState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AgentActivityState> _activityStates = new(StringComparer.Ordinal);
        private string? _workspaceLabel;
        private int _filesScanned;
        private int _filesChanged;
        private bool _hasVerifiedMutation;
        private bool _hasUnverifiedMutation;

        public string? WorkspaceLabel
        {
            get
            {
                lock (_gate)
                {
                    return _workspaceLabel;
                }
            }
        }

        public void SetWorkspaceLabel(string? label)
        {
            lock (_gate)
            {
                _workspaceLabel ??= label;
            }
        }

        public void Apply(AgentActivity activity)
        {
            lock (_gate)
            {
                if (_activityStates.TryGetValue(activity.ActivityId, out var previous)
                    && (previous is AgentActivityState.Completed or AgentActivityState.Failed))
                {
                    return;
                }

                _activityStates[activity.ActivityId] = activity.State;
                if (activity.State is not (AgentActivityState.Completed or AgentActivityState.Failed))
                {
                    return;
                }

                if (activity.Kind == AgentActivityKind.Explore && activity.State == AgentActivityState.Completed)
                {
                    _filesScanned = SaturatingAdd(_filesScanned, activity.Count ?? 0);
                }

                if (activity.Kind == AgentActivityKind.Edit)
                {
                    if (activity.State == AgentActivityState.Completed)
                    {
                        _filesChanged = SaturatingAdd(_filesChanged, 1);
                        _hasVerifiedMutation = true;
                    }
                    else
                    {
                        _hasUnverifiedMutation = true;
                    }
                }
            }
        }

        public AgentRunSummaryMetrics ToMetrics()
        {
            lock (_gate)
            {
                return new AgentRunSummaryMetrics(
                    _filesScanned == 0 ? null : _filesScanned,
                    _filesChanged == 0 ? null : _filesChanged,
                    null,
                    null,
                    _hasVerifiedMutation,
                    _hasUnverifiedMutation);
            }
        }

        private static int SaturatingAdd(int left, int right) =>
            right <= 0 ? left : Math.Min(AgentRunSummary.MaximumCount, left + right);
    }
}
