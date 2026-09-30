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

public sealed class AgentRunController : INotifyPropertyChanged, IDisposable
{
    private readonly IAgentRunClient _client;
    private readonly HarnessEventSink? _sink;
    private readonly Action<Action>? _synchronize;
    private readonly Action<AgentEventModel>? _auditSink;
    private readonly object _gate = new();
    private readonly HashSet<string> _assistantDeltaKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolOutputKeys = new(StringComparer.Ordinal);

    // The busy flag and status line live in OperationStatus, shared with the policy
    // controller so both present one shape to the view. The `using static` below keeps
    // the existing assignments and reads unchanged.
    private readonly OperationStatus _status;

    private AgentRunModel? _currentRun;
    private string? _currentRunId;
    private string? _waitKind;

    /// <summary>
    /// Wait kind for a run the host parked in ReconciliationRequired. Matches the
    /// durable <c>run.state_changed</c> safe summary the host publishes for it.
    /// </summary>
    private const string ReconciliationWaitKind = "reconciliation_required";

    private string? _pendingApprovalId;
    private string? _pendingApprovalSummary;
    private string? _pendingApprovalCommand;
    private string? _streamTerminalStatus;
    private bool _isCancelRequested;
    private CancellationTokenSource? _subscriptionCts;

    // Why a transient frame never reached the view. Every one of these gates is a
    // silent `return`, so a reply can lose its tail with no trace at all -- the
    // symptom is a completed turn with a blank bubble, which reads like a very short
    // answer. The counters make the loss countable instead of a guess, and name the
    // gate that caused it. Read-only for the view; only the gates below write.
    private int _droppedDeltasAtIngress;
    private int _droppedDeltasAtUiGate;
    private int _droppedDeltasAsDuplicates;
    private int _droppedDeltasAfterCancel;

    /// <summary>
    /// Transient assistant deltas this controller refused, split by the gate that
    /// refused them. Non-zero means the transcript is missing text the run produced.
    /// </summary>
    public AgentTransientDropReport AssistantDeltaDrops =>
        new(
            _droppedDeltasAtIngress,
            _droppedDeltasAtUiGate,
            _droppedDeltasAsDuplicates,
            _droppedDeltasAfterCancel);

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

    public string? PendingApprovalId
    {
        get { lock (_gate) return _pendingApprovalId; }
        private set
        {
            lock (_gate)
            {
                _pendingApprovalId = value;
            }

            OnPropertyChanged(nameof(PendingApprovalId));
            OnPropertyChanged(nameof(HasPendingApproval));
        }
    }

    public string? PendingApprovalSummary
    {
        get { lock (_gate) return _pendingApprovalSummary; }
        private set
        {
            lock (_gate)
            {
                _pendingApprovalSummary = value;
            }

            OnPropertyChanged(nameof(PendingApprovalSummary));
        }
    }

    public string? PendingApprovalCommand
    {
        get { lock (_gate) return _pendingApprovalCommand; }
        private set
        {
            lock (_gate)
            {
                _pendingApprovalCommand = value;
            }

            OnPropertyChanged(nameof(PendingApprovalCommand));
        }
    }

    public bool HasPendingApproval => PendingApprovalId is not null;

    public bool NeedsWorkspaceTrust =>
        CurrentRun?.Status == "waiting_workspace_trust"
        && !string.IsNullOrWhiteSpace(CurrentRun.TrustRequestId)
        && !string.IsNullOrWhiteSpace(CurrentRun.DisplayIdentityHash);
    public bool CanCancel
    {
        get { lock (_gate) return _currentRunId is not null && !_isCancelRequested; }
    }

    public bool CanContinue
    {
        get
        {
            lock (_gate)
            {
                // A run parked on reconciliation also resumes through Continue: the
                // host resolves it as an inspection-gated resume. Without this the
                // panel offers Stop only and the run can never be resumed.
                return _currentRunId is not null
                    && (_waitKind == "waiting_user" || _waitKind == ReconciliationWaitKind)
                    && !_isCancelRequested;
            }
        }
    }

    public async Task CreateAndRunAsync(CreateAgentRunRequest request, CancellationToken externalCancellation = default)
    {
        bool alreadyBusy;
        lock (_gate)
        {
            alreadyBusy = _isBusy;
            if (!alreadyBusy)
            {
                _isBusy = true;
            }
        }

        if (alreadyBusy)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        lock (_gate)
        {
            _subscriptionCts = cts;
        }

        var token = cts.Token;

        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            _isCancelRequested = false;
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            Events.Clear();
            lock (_gate)
            {
                _assistantDeltaKeys.Clear();
                _toolOutputKeys.Clear();
                _streamTerminalStatus = null;
            }
            RefreshCommands();
        });

        try
        {
            var run = await _client.CreateRunAsync(request, token).ConfigureAwait(false);
            // Hand the run id over before subscribing: the delta/activity handlers
            // read it on the SSE thread, and a UI-queued assignment lets the first
            // frames (a fast model's opening delta) arrive while it is still null
            // and be dropped.
            lock (_gate)
            {
                _currentRunId = run.RunId;
            }

            RunOnUi(() =>
            {
                CurrentRun = run;
                _waitKind = WaitKindFor(run);
                _statusText = $"Running: {run.RunId}";
                RefreshCommands();
            });

            await FollowRunStreamAsync(run.RunId, 0, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RunOnUi(() =>
            {
                _statusText = "Event monitoring stopped.";
                _isBusy = false;
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                // The stream died before the run's terminal snapshot, so the host
                // may still own the run. Keep the run reference (Stop stays
                // available) and say what the user can do instead of leaving a
                // silent dead end.
                _statusText = string.Equals(ex.GetType().Name, "OperationCanceledException", StringComparison.Ordinal)
                    ? "Event monitoring stopped."
                    : "Event stream ended unexpectedly. Use Stop to end the run, then send again.";
                _isBusy = false;
                RefreshCommands();
            });
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Takes over a run this controller did not create. A host restart leaves a
    /// session's newest run parked or live with no client following it; opening the
    /// session must offer Continue or Stop for exactly that run instead of leaving
    /// it invisible. The stream is followed from the run's durable cursor in the
    /// background, so opening a session never waits on a run that may stay parked
    /// indefinitely. Returns <c>true</c> when this call latched the run and started
    /// its follow; <c>false</c> when adoption was skipped because another run
    /// already owns the controller or the open token was already cancelled.
    /// </summary>
    public Task<bool> AdoptRunAsync(AgentRunModel run, CancellationToken externalCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // A session open already cancelled (the user switched away) must not latch
        // a run: the follow would die on the cancelled token while the reference
        // kept the next session from adopting its own run. Checked before the latch
        // so a skipped adoption leaves the controller exactly as it was.
        if (externalCancellation.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            // A run already in hand -- or a create in flight that is about to set
            // one -- owns the subscription; adoption is only for the idle controller
            // a fresh session open starts from. A skipped adoption is a bounded
            // no-op: the caller fail-softs and the session still opens.
            if (_currentRunId is not null || _isBusy)
            {
                return Task.FromResult(false);
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        lock (_gate)
        {
            _subscriptionCts = cts;
            // Hand the run id over before the first UI frame: the delta/activity
            // handlers read it on the SSE thread, and a UI-queued assignment lets
            // the opening frames arrive while it is still null and be dropped.
            _currentRunId = run.RunId;
        }

        var token = cts.Token;
        _isBusy = true;

        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            _isCancelRequested = false;
            _waitKind = WaitKindFor(run);
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            Events.Clear();
            lock (_gate)
            {
                _assistantDeltaKeys.Clear();
                _toolOutputKeys.Clear();
                _streamTerminalStatus = null;
            }

            CurrentRun = run;
            _statusText = AdoptedStatusText(run);
            RefreshCommands();
        });

        _ = FollowAdoptedRunAsync(run.RunId, run.LastSequence, token, cts);
        return Task.FromResult(true);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        string? runId = BeginRunTeardown();
        if (runId is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            _statusText = "Cancelling run...";
            RefreshCommands();
        });

        try
        {
            await _client.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
            ReleaseCurrentRun(runId, "Cancellation request sent.");
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                _statusText = $"Cancel error: {ex.Message}";
                _isBusy = false;
                RefreshCommands();
            });
        }
    }

    /// <summary>
    /// Whether the run in hand is parked for the user to resume rather than being
    /// actively driven. A session switch leaves such a run alone on the host.
    /// </summary>
    public bool IsCurrentRunParked
    {
        get
        {
            lock (_gate)
            {
                return _currentRunId is not null
                    && (_waitKind == "waiting_user" || _waitKind == ReconciliationWaitKind);
            }
        }
    }

    /// <summary>
    /// Ends this controller's involvement for a session switch: a parked run is left
    /// parked on the host (it is the user's to resume from its transcript) and only
    /// the follow is detached; a live run is still cancelled, which is what a session
    /// switch always did.
    /// </summary>
    public Task StopForSessionSwitchAsync(CancellationToken cancellationToken = default) =>
        IsCurrentRunParked ? DetachCurrentRunAsync() : CancelAsync(cancellationToken);

    /// <summary>
    /// Drops the current run without cancelling it on the host: the follow stream is
    /// torn down, the run reference is cleared, and no cancel request is posted. The
    /// run keeps its parked state server-side, so opening the session again adopts
    /// it. The async shape matches <see cref="CancelAsync"/> for a session-switch
    /// site that awaits the teardown.
    /// </summary>
    public Task DetachCurrentRunAsync()
    {
        string? runId = BeginRunTeardown();
        if (runId is null)
        {
            return Task.CompletedTask;
        }

        // The non-cancel half of CancelAsync's teardown: same clearing and same
        // idle transition, minus the POST and with copy that says the run survives.
        ReleaseCurrentRun(runId, "Run detached. It stays parked on the host.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts tearing the current run down: the follow stream is cancelled and the
    /// run is marked as no longer driven. Shared by cancel and detach; returns the
    /// run id being torn down, or null when there is nothing to tear down.
    /// </summary>
    private string? BeginRunTeardown()
    {
        lock (_gate)
        {
            string? runId = _currentRunId;
            if (runId is not null)
            {
                _subscriptionCts?.Cancel();
                _isCancelRequested = true;
            }

            return runId;
        }
    }

    /// <summary>
    /// Clears the current run reference on the UI thread and returns the controller
    /// to idle. Shared by cancel (the host no longer owns the run) and detach (the
    /// run keeps running under the session that parked it). The run-id check keeps a
    /// stale completion from clearing a newer run.
    /// </summary>
    private void ReleaseCurrentRun(string runId, string statusText)
    {
        RunOnUi(() =>
        {
            if (string.Equals(_currentRunId, runId, StringComparison.Ordinal))
            {
                _currentRunId = null;
                _waitKind = null;
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
                CurrentRun = null;
            }

            _statusText = statusText;
            _isBusy = false;
            RefreshCommands();
        });
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        string? runId;
        lock (_gate)
        {
            runId = _currentRunId;
        }

        if (runId is null)
        {
            return;
        }

        try
        {
            await _client.ContinueAsync(runId, cancellationToken).ConfigureAwait(false);
            RunOnUi(() =>
            {
                _waitKind = null;
                _statusText = "Resume request sent.";
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                _statusText = $"Resume error: {ex.Message}";
                RefreshCommands();
            });
        }
    }
    public async Task TrustAsync(bool approve, CancellationToken cancellationToken = default)
    {
        var run = CurrentRun;
        if (run is null
            || string.IsNullOrWhiteSpace(run.TrustRequestId)
            || string.IsNullOrWhiteSpace(run.DisplayIdentityHash))
        {
            throw new InvalidOperationException("No workspace trust request is pending.");
        }

        RunOnUi(() => _statusText = approve
            ? "Confirming workspace..."
            : "Rejecting workspace...");
        await _client.TrustAsync(
            run.RunId,
            new TrustAgentRunRequest(
                run.TrustRequestId,
                run.DisplayIdentityHash,
                approve ? "approve" : "deny"),
            cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => _statusText = "Submitting approval...");
        // Session permission is decided server-side: Host picks the right
        // one-time token (session directory grant or ephemeral terminal token)
        // from the pending approval binding. The UI never selects a scope.
        await _client.ApproveAsync(runId, approvalId, "once", cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves an external-directory request for the whole chat session. Only
    /// offered when the pending ask reason is external_directory_required; the
    /// server fails anything else closed and the grant dies with the session.
    /// </summary>
    public async Task ApproveSessionDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => _statusText = "Submitting approval...");
        await _client.ApproveAsync(runId, approvalId, "session", cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves once and remembers the exact intent as a project rule. Offered
    /// for known non-external asks; the server fails external-directory asks
    /// closed for this scope and unknown reasons stay one-time.
    /// </summary>
    public async Task ApproveAlwaysAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => _statusText = "Submitting approval...");
        await _client.ApproveAsync(runId, approvalId, "always", cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => _statusText = "Denying approval...");
        await _client.DenyAsync(runId, approvalId, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        if (runId is null)
        {
            return;
        }

        var run = await _client.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (!string.Equals(_currentRunId, run.RunId, StringComparison.Ordinal))
            {
                return;
            }

            CurrentRun = run;
            _waitKind = WaitKindFor(run);
            if (!string.Equals(run.Status, "waiting_approval", StringComparison.Ordinal))
            {
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
            }

            RefreshCommands();
        });
    }
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



    private async Task OnEventAsync(AgentEventModel model)
    {
        if (model.IsTransient)
        {
            // Raw transient output is intentionally not part of the durable event
            // collection. AgentActivityModel travels through the separate callback.
            return;
        }

        AgentModelUsage? usage = null;
        if (string.Equals(model.EventType, "model.usage_recorded", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(model.PayloadRef))
        {
            CancellationToken fetchToken;
            try
            {
                fetchToken = _subscriptionCts?.Token ?? CancellationToken.None;
            }
            catch (ObjectDisposedException)
            {
                fetchToken = CancellationToken.None;
            }

            try
            {
                var payload = await _client
                    .GetPayloadAsync(model.RunId, model.PayloadRef, fetchToken)
                    .ConfigureAwait(false);
                usage = AgentModelUsage.Parse(payload.Content);
            }
            catch (OperationCanceledException)
            {
                // Subscription torn down; usage stays null for this event.
            }
            catch (Exception exception)
            {
                // Usage is observational. A payload fetch failure must not stop the
                // durable run stream or alter the assistant reply. Log the failure
                // type only (no identifiers or payload content) for diagnostics.
                GuiDiagnosticWriter.AppendTraceLine(
                    $"Agent usage payload fetch failed: {exception.GetType().Name}");
            }
        }

        if (string.Equals(model.EventType, "final_snapshot", StringComparison.Ordinal)
            && IsTerminalStatus(model.Status))
        {
            lock (_gate)
            {
                _streamTerminalStatus = model.Status;
            }
        }

        RunOnUi(() =>
        {
            if (Events.Count > 200)
            {
                Events.RemoveAt(0);
            }

            Events.Add(model);
            if (!string.IsNullOrEmpty(model.SafeSummary)
                && !IsApprovalRequestWithPanel(model))
            {
                _statusText = model.SafeSummary;
            }

            if (!model.IsTransient
                && model.EventType == "continue.requested")
            {
                // The frame's status decides, exactly like the TUI: the park commit
                // carries waiting_user, while the "continued" commit flips the run
                // back to running. Keying every frame to waiting_user left a live run
                // parked in the client until the monitor's next refresh -- Continue
                // earned a 409, and a session switch detached a run nobody followed.
                if (string.Equals(model.Status, "waiting_user", StringComparison.Ordinal))
                {
                    _waitKind = "waiting_user";
                }
                else if (string.Equals(model.Status, "running", StringComparison.Ordinal))
                {
                    _waitKind = null;
                }
            }

            // The host parks a run in ReconciliationRequired after an ambiguous
            // tool outcome and stops driving it. Surface that as a resumable wait
            // so the user gets a Continue affordance instead of a silent panel.
            if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, ReconciliationWaitKind, StringComparison.Ordinal))
            {
                _waitKind = ReconciliationWaitKind;
                _statusText = "Run paused: the last action was interrupted. Send Continue to resume with a read-only inspection.";
            }

            if (!model.IsTransient
                && model.EventType == "approval.requested"
                && !string.IsNullOrWhiteSpace(model.ApprovalId))
            {
                PendingApprovalId = model.ApprovalId;
                PendingApprovalSummary = model.SafeSummary;
            }
            else if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, "approval_resolved", StringComparison.Ordinal))
            {
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
            }

            if (!model.IsTransient)
            {
                // AgentAuditLog is UI-bound through the composition. Keep both the
                // collection mutation and sink write on the same dispatcher as the
                // event list to avoid cross-thread ObservableCollection access.
                _auditSink?.Invoke(model);
                WriteSink(model);
            }

            if (usage is not null)
            {
                UsageReceived?.Invoke(
                    this,
                    new AgentUsageReceivedEventArgs(model.RunId, model.SessionId, usage));
            }

            RefreshCommands();
        });

        await Task.CompletedTask;
    }

    private Task OnActivityAsync(AgentActivityModel model)
    {
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                if (model.Kind == AgentActivityKind.Terminal && !string.IsNullOrWhiteSpace(model.Detail))
                {
                    PendingApprovalCommand = model.Detail;
                }

                ActivityReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual activity subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnSummaryAsync(AgentRunSummaryModel model)
    {
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                SummaryReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual summary subscriber is observational and must not fail the
                // Agent SSE subscription or durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnAssistantDeltaAsync(AgentAssistantDeltaModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? runId;
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested || _currentRunId is null)
            {
                if (!_isCancelRequested)
                {
                    Interlocked.Increment(ref _droppedDeltasAtIngress);
                }
                else
                {
                    Interlocked.Increment(ref _droppedDeltasAfterCancel);
                }

                return Task.CompletedTask;
            }

            runId = _currentRunId;
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        if (_isCancelRequested)
                        {
                            Interlocked.Increment(ref _droppedDeltasAfterCancel);
                        }
                        else
                        {
                            Interlocked.Increment(ref _droppedDeltasAtUiGate);
                        }

                        return;
                    }

                    var key = $"{runId}:{model.ModelStepId}:{model.TransientSequence}";
                    if (!_assistantDeltaKeys.Add(key))
                    {
                        Interlocked.Increment(ref _droppedDeltasAsDuplicates);
                        return;
                    }
                }

                AssistantDeltaReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual delta subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnToolOutputAsync(AgentToolOutputModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? runId;
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested || _currentRunId is null)
            {
                return Task.CompletedTask;
            }

            runId = _currentRunId;
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var key = $"{runId}:{model.ExecutionId}:{model.TransientSequence}";
                    if (!_toolOutputKeys.Add(key))
                    {
                        return;
                    }
                }

                ToolOutputReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual tool-output subscriber is observational and must not
                // fail the Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private void WriteSink(AgentEventModel model)
    {
        if (_sink is null)
        {
            return;
        }

        var code = model.EventType switch
        {
            "approval.requested" => "agent.approval.required",
            "run.failed" or "run.state_changed" when model.Status == "cancelled" => "agent.cancelled",
            _ => "agent.activity",
        };

        try
        {
            _sink.Write(code, new Dictionary<string, object?>
            {
                ["event_type"] = model.EventType,
                ["safe_summary"] = model.SafeSummary,
                ["sequence"] = model.Sequence,
            });
        }
        catch
        {
            // sink allowlist/validation failures must not break the run feed
        }
    }

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

    public event EventHandler<AgentActivityModel>? ActivityReceived;

    public event EventHandler<AgentRunSummaryModel>? SummaryReceived;

    public event EventHandler<AgentAssistantDeltaModel>? AssistantDeltaReceived;

    public event EventHandler<AgentToolOutputModel>? ToolOutputReceived;

    public event EventHandler<AgentUsageReceivedEventArgs>? UsageReceived;

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

        return $"Running: {run.RunId}";
    }

    /// <summary>
    /// Follows one run's event stream and settles the controller when the stream
    /// ends. Shared by the create path and adoption so a run is followed by identical
    /// logic either way. <paramref name="afterSequence"/> is the durable cursor to
    /// resume from: a created run starts at 0, an adopted one at its LastSequence so
    /// frames the session open already replayed are not delivered twice.
    /// <paramref name="onFrame"/> fires for every inbound frame so the adopted follow
    /// can tell "cancelled before anything arrived" from "cancelled after the run
    /// already materialized".
    /// </summary>
    private async Task FollowRunStreamAsync(
        string runId,
        long afterSequence,
        CancellationToken token,
        Action? onFrame = null)
    {
        await _client.SubscribeEventsAsync(
                runId,
                afterSequence,
                model =>
                {
                    onFrame?.Invoke();
                    return OnEventAsync(model);
                },
                token,
                model =>
                {
                    onFrame?.Invoke();
                    return OnActivityAsync(model);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnSummaryAsync(model);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnAssistantDeltaAsync(model);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnToolOutputAsync(model);
                })
            .ConfigureAwait(false);
        // The SSE stream ends after the terminal snapshot, but the create response
        // may be stale (or the stream may have disconnected). Refresh the durable
        // run before changing command state so Cancel is never left enabled for a
        // completed run and a disconnect is not reported as success.
        try
        {
            await RefreshAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested && TryApplyTerminalStreamSnapshot())
        {
            // The SSE stream already delivered the authoritative terminal
            // snapshot. A follow-up GET can race backend shutdown/restart; do
            // not turn a completed reply into a false client-side failure.
        }

        RunOnUi(() =>
        {
            var terminal = IsTerminalStatus(CurrentRun?.Status);
            if (terminal)
            {
                _currentRunId = null;
                _waitKind = null;
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
                if (!_isCancelRequested
                    && !string.Equals(CurrentRun?.Status, "failed", StringComparison.Ordinal))
                {
                    _statusText = string.Empty;
                }
            }
            else if (!_isCancelRequested)
            {
                _statusText = _waitKind switch
                {
                    "waiting_user" => "Waiting to continue. Type Continue to resume.",
                    ReconciliationWaitKind => "Run paused: the last action was interrupted. Send Continue to resume with a read-only inspection.",
                    _ => "Event stream ended before run completed.",
                };
            }

            _isBusy = false;
            RefreshCommands();
        });
    }

    /// <summary>
    /// Runs the adopted run's stream in the background. Unlike the create path there
    /// is no caller to surface a fault to, so every failure is settled into the
    /// controller instead of being rethrown; the run reference is kept so Stop stays
    /// available when the host may still own the run.
    /// </summary>
    private async Task FollowAdoptedRunAsync(
        string runId,
        long afterSequence,
        CancellationToken token,
        CancellationTokenSource cts)
    {
        var sawFrame = false;
        try
        {
            await FollowRunStreamAsync(runId, afterSequence, token, () => sawFrame = true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A cancelled session open kills the follow before the run ever produced
            // anything: the latch must release so the session now opening can adopt
            // its own run instead of being blocked by this stale reference forever.
            // Once a frame arrived the run did materialize, so Stop must stay.
            if (!sawFrame && string.Equals(_currentRunId, runId, StringComparison.Ordinal))
            {
                ReleaseCurrentRun(runId, "Event monitoring stopped.");
            }
            else
            {
                RunOnUi(() =>
                {
                    _statusText = "Event monitoring stopped.";
                    _isBusy = false;
                    RefreshCommands();
                });
            }
        }
        catch (Exception)
        {
            RunOnUi(() =>
            {
                _statusText = "Event stream ended unexpectedly. Use Stop to end the run, then send again.";
                _isBusy = false;
                RefreshCommands();
            });
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private bool TryApplyTerminalStreamSnapshot()
    {
        string? status;
        string? runId;
        lock (_gate)
        {
            status = _streamTerminalStatus;
            runId = _currentRunId;
        }

        if (!IsTerminalStatus(status) || string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        RunOnUi(() =>
        {
            if (!string.Equals(_currentRunId, runId, StringComparison.Ordinal)
                || CurrentRun is not { } current)
            {
                return;
            }

            CurrentRun = current with { Status = status! };
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            RefreshCommands();
        });
        return true;
    }

    public void Dispose()
    {
        CancellationTokenSource? subscription;
        lock (_gate)
        {
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
