using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;

using RuApp.Services;

namespace RuApp.Services;

/// <summary>
/// Why transient assistant deltas never reached the view. Every gate that refuses a
/// frame is otherwise a silent <c>return</c>, so a reply can lose its tail with no
/// trace; a non-zero count here is the difference between "the model said little" and
/// "the transcript lost what the model said".
/// </summary>
/// <param name="AtIngress">Refused on the SSE thread: the run was no longer the active one.</param>
/// <param name="AtUiGate">Refused on the UI thread because a different run had taken over the view.</param>
/// <param name="AsDuplicates">The same run/step/sequence had already been delivered.</param>
/// <param name="AfterCancel">The operator cancelled, so the tail is intentionally dropped.</param>
public readonly record struct AgentTransientDropReport(
    int AtIngress,
    int AtUiGate,
    int AsDuplicates,
    int AfterCancel)
{
    /// <summary>Drops that lost text the run had already produced.</summary>
    public int Lost => AtIngress + AtUiGate;
}

public sealed partial class AgentRunController : INotifyPropertyChanged, IDisposable
{
    private readonly IAgentRunClient _client;
    public ISubagentClient? SubagentClient => _client as ISubagentClient;
    private readonly HarnessEventSink? _sink;
    private readonly Action<Action>? _synchronize;
    private readonly Action<AgentEventModel>? _auditSink;
    private readonly object _gate = new();

    // The busy flag and status line live in OperationStatus, shared with the policy
    // controller so both present one shape to the view. The `using static` below keeps
    // the existing assignments and reads unchanged.
    private readonly OperationStatus _status;

    private AgentRunModel? _currentRun;
    private string? _waitKind;

    /// <summary>
    /// Wait kind for a run the host parked in ReconciliationRequired. Matches the
    /// durable <c>run.state_changed</c> safe summary the host publishes for it.
    /// </summary>
    private const string ReconciliationWaitKind = "reconciliation_required";

    private bool _isCancelRequested;

    /// <summary>
    /// Transient assistant deltas this controller refused, split by the gate that
    /// refused them. Non-zero means the transcript is missing text the run produced.
    /// </summary>
    public AgentTransientDropReport AssistantDeltaDrops => _feedState.Drops;

    public AgentRunController(IAgentRunClient client, HarnessEventSink? sink = null, Action<Action>? synchronize = null, Action<AgentEventModel>? auditSink = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sink = sink;
        _synchronize = synchronize;
        _auditSink = auditSink;
        _status = new OperationStatus(RunOnUi, "Ready");
        _status.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);
    }

    public ObservableCollection<AgentEventModel> Events { get; } = new();

    /// <summary>True while a run is being driven. See <see cref="OperationStatus"/>.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>The line the composer shows. See <see cref="OperationStatus"/>.</summary>
    public string StatusText => _statusText;

    // Internal aliases so the assignments below read as they always did while the
    // state itself lives in OperationStatus.
    private bool _isBusy
    {
        get => _status.IsBusy;
        set => _status.SetBusy(value);
    }

    private string _statusText
    {
        get => _status.StatusText;
        set => _status.SetStatus(value);
    }

    public AgentRunModel? CurrentRun
    {
        get { lock (_gate) return _currentRun; }
        private set
        {
            lock (_gate)
            {
                _currentRun = value;
            }

            OnPropertyChanged(nameof(CurrentRun));
            OnPropertyChanged(nameof(NeedsWorkspaceTrust));
        }
    }

    public bool NeedsWorkspaceTrust =>
        CurrentRun?.Status == "waiting_workspace_trust"
        && !string.IsNullOrWhiteSpace(CurrentRun.TrustRequestId)
        && !string.IsNullOrWhiteSpace(CurrentRun.DisplayIdentityHash);
    public Task<long> SetSessionPresetAsync(
        string sessionId,
        string preset,
        long expectedVersion,
        bool interactive,
        CancellationToken cancellationToken = default) =>
        _client.SetSessionPresetAsync(
            sessionId,
            preset,
            expectedVersion,
            interactive,
            cancellationToken);



    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanContinue));
    }

    /// <summary>
    /// Whether the event opens the approval panel. Its machine code must not
    /// reach the status line: the panel already speaks for the request, and the
    /// code alone only prints one more raw token row.
    /// </summary>
    private static bool IsApprovalRequestWithPanel(AgentEventModel model) =>
        string.Equals(model.EventType, "approval.requested", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(model.ApprovalId);

    /// <summary>
    /// Whether the event opens the question panel. Its machine code must not
    /// reach the status line for the same reason the approval code must not:
    /// the panel already speaks for the request, and the raw code is only a
    /// fallback when the request payload itself could not be read.
    /// </summary>
    private static bool IsInputRequestEvent(AgentEventModel model) =>
        string.Equals(model.EventType, "input.requested", StringComparison.Ordinal);

    private void RunOnUi(Action action)
    {
        if (_synchronize is not null)
        {
            _synchronize(action);
        }
        else
        {
            action();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<AgentRunActivityEventArgs>? ActivityReceived;

    public event EventHandler<AgentRunSummaryEventArgs>? SummaryReceived;

    public bool IsCurrentFeedOwner(string runId, long ownershipGeneration)
    {
        lock (_gate)
        {
            return OwnsFeedLocked(runId, ownershipGeneration);
        }
    }

    private bool OwnsFeedLocked(string runId, long ownershipGeneration) =>
        _runOwnershipGeneration == ownershipGeneration
        && (string.Equals(_currentRunId, runId, StringComparison.Ordinal)
            || (_currentRunId is null && !_isBusy));

    public event EventHandler<AgentAssistantDeltaModel>? AssistantDeltaReceived;

    public event EventHandler<AgentToolOutputModel>? ToolOutputReceived;

    public event EventHandler<AgentUsageReceivedEventArgs>? UsageReceived;
    public event EventHandler<AgentContextEstimateEventArgs>? ContextEstimateReceived;

    public event EventHandler<AgentRunContextCompactedEventArgs>? ContextCompacted;
    public event EventHandler<AgentRunAdoptedEventArgs>? RunAdopted;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool IsTerminalStatus(string? status) => status is
        "completed" or "failed" or "cancelled" or "interrupted";

    /// <summary>
    /// Derives the wait kind the command surface acts on from a durable run. A run
    /// parked on reconciliation is as resumable as one parked on WaitingUser, but
    /// it carries no HistoryWaitKind, and a WaitingUser run may report no history
    /// kind either, so both are read from the status string; everything else falls
    /// back to the outbox wait kind.
    /// </summary>
    private static string? WaitKindFor(AgentRunModel? run) =>
        run is null
            ? null
            : run.Status is "waiting_user" or ReconciliationWaitKind
                ? run.Status
                : run.HistoryWaitKind;

    /// <summary>
    /// The composer line for an adopted run: a parked run gets its resume copy, a
    /// live one the same running line a created run shows.
    /// </summary>
    private static string AdoptedStatusText(AgentRunModel run)
    {
        if (string.Equals(run.Status, ReconciliationWaitKind, StringComparison.Ordinal))
        {
            return "Run paused: the last action was interrupted. Send Continue to resume with a read-only inspection.";
        }

        if (string.Equals(run.Status, "waiting_user", StringComparison.Ordinal))
        {
            return "Waiting to continue. Type Continue to resume.";
        }

        if (string.Equals(run.Status, "waiting_approval", StringComparison.Ordinal))
        {
            // GET /runs/{id} carries no approval id, so adoption cannot re-arm the
            // approval panel. Say where the approval actually is instead of claiming
            // the run is merely running; Stop stays available from the same line.
            return $"Waiting for approval: {run.RunId} — review the approval in the session (or cancel the run)";
        }

        if (string.Equals(run.Status, "waiting_input", StringComparison.Ordinal))
        {
            // Same shape as the approval wait: the question card is re-armed from
            // the replayed run log, and this line says what the run is waiting on
            // until it is.
            return $"Waiting for input: {run.RunId} — answer the question in the session (or cancel the run)";
        }

        return $"Running: {run.RunId}";
    }

    public void Dispose()
    {
        CancellationTokenSource? subscription;
        lock (_gate)
        {
            _runOwnershipGeneration++;
            subscription = _subscriptionCts;
            _subscriptionCts = null;
            subscription?.Cancel();
        }

        subscription?.Dispose();
        if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
