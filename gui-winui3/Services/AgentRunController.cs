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
    public ISubagentClient? SubagentClient => _client as ISubagentClient;
    private readonly HarnessEventSink? _sink;
    private readonly Action<Action>? _synchronize;
    private readonly Action<AgentEventModel>? _auditSink;
    private readonly object _gate = new();
    private readonly AgentRunDeltaDeduplicator _deltaDeduplicator = new();

    // The busy flag and status line live in OperationStatus, shared with the policy
    // controller so both present one shape to the view. The `using static` below keeps
    // the existing assignments and reads unchanged.
    private readonly OperationStatus _status;

    private AgentRunModel? _currentRun;
    private readonly AgentRunApprovalCoordinator _approvalCoordinator = new();
    private readonly AgentRunInputCoordinator _inputCoordinator = new();
    private string? _currentRunId;
    private CreateAgentRunRequest? _pendingCreateRequest;
    private TaskCompletionSource? _pendingCreateSettled;
    private string? _waitKind;
    private long _runOwnershipGeneration;

    /// <summary>
    /// Wait kind for a run the host parked in ReconciliationRequired. Matches the
    /// durable <c>run.state_changed</c> safe summary the host publishes for it.
    /// </summary>
    private const string ReconciliationWaitKind = "reconciliation_required";

    private string? _streamTerminalStatus;
    private bool _isCancelRequested;
    private bool _isPendingCreateOutcomeUnknown;
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
        get { lock (_gate) return _approvalCoordinator.Id; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetId(value);
            }

            OnPropertyChanged(nameof(PendingApprovalId));
            OnPropertyChanged(nameof(HasPendingApproval));
        }
    }

    public string? PendingApprovalSummary
    {
        get { lock (_gate) return _approvalCoordinator.Summary; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetSummary(value);
            }

            OnPropertyChanged(nameof(PendingApprovalSummary));
        }
    }

    public string? PendingApprovalCommand
    {
        get { lock (_gate) return _approvalCoordinator.Command; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetCommand(value);
            }

            OnPropertyChanged(nameof(PendingApprovalCommand));
        }
    }

    public bool HasPendingApproval => PendingApprovalId is not null;

    public string? PendingInputId
    {
        get { lock (_gate) return _inputCoordinator.Id; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetId(value);
            }

            OnPropertyChanged(nameof(PendingInputId));
            OnPropertyChanged(nameof(HasPendingInput));
        }
    }

    public string? PendingInputQuestion
    {
        get { lock (_gate) return _inputCoordinator.Question; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetQuestion(value);
            }

            OnPropertyChanged(nameof(PendingInputQuestion));
        }
    }

    public IReadOnlyList<string> PendingInputOptions
    {
        get { lock (_gate) return _inputCoordinator.Options; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetOptions(value);
            }

            OnPropertyChanged(nameof(PendingInputOptions));
        }
    }

    public bool HasPendingInput => PendingInputId is not null;

    /// <summary>The Host's remembered approval rules, when the client can reach them.</summary>
    internal IApprovalRulesClient? ApprovalRules => _client as IApprovalRulesClient;

    public bool NeedsWorkspaceTrust =>
        CurrentRun?.Status == "waiting_workspace_trust"
        && !string.IsNullOrWhiteSpace(CurrentRun.TrustRequestId)
        && !string.IsNullOrWhiteSpace(CurrentRun.DisplayIdentityHash);
    public bool CanCancel
    {
        get
        {
            lock (_gate)
            {
                return !_isCancelRequested
                    && (_currentRunId is not null || (_isPendingCreateOutcomeUnknown && !_isBusy));
            }
        }
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

    public async Task<bool> CreateAndRunAsync(
        CreateAgentRunRequest request,
        CancellationToken externalCancellation = default,
        Action? onAccepted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestSnapshot = SnapshotCreateRequest(request);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        var createSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool alreadyBusy;
        long ownershipGeneration = 0;
        lock (_gate)
        {
            alreadyBusy = _isBusy || _isPendingCreateOutcomeUnknown;
            if (!alreadyBusy)
            {
                _isBusy = true;
                _subscriptionCts = cts;
                _pendingCreateRequest = requestSnapshot;
                _pendingCreateSettled = createSettled;
                ownershipGeneration = ++_runOwnershipGeneration;
                _deltaDeduplicator.Reset();
                _streamTerminalStatus = null;
            }
        }

        if (alreadyBusy)
        {
            cts.Dispose();
            return false;
        }

        var token = cts.Token;

        RunOnUi(() =>
        {
            if (_runOwnershipGeneration != ownershipGeneration) return;
            OnPropertyChanged(nameof(IsBusy));
            _isCancelRequested = false;
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            PendingInputId = null;
            PendingInputQuestion = null;
            PendingInputOptions = Array.Empty<string>();
            Events.Clear();
            RefreshCommands();
        });

        bool accepted = false;
        try
        {
            var run = await _client.CreateRunAsync(requestSnapshot, token).ConfigureAwait(false);
            bool wasCancelled;
            // Hand the run id over before subscribing: the delta/activity handlers
            // read it on the SSE thread, and a UI-queued assignment lets the first
            // frames (a fast model's opening delta) arrive while it is still null
            // and be dropped.
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCreateRequest, requestSnapshot))
                {
                    _pendingCreateRequest = null;
                    _isPendingCreateOutcomeUnknown = false;
                }
                wasCancelled = token.IsCancellationRequested || !ReferenceEquals(_subscriptionCts, cts);
                if (!wasCancelled)
                {
                    _currentRunId = run.RunId;
                }
            }

            if (wasCancelled)
            {
                await CancelLateCreatedRunAsync(run.RunId, cts).ConfigureAwait(false);
                return false;
            }

            PublishCreatedRun(run, ownershipGeneration, token, onAccepted);
            accepted = true;
            createSettled.TrySetResult();
            await FollowRunStreamAsync(run.RunId, 0, ownershipGeneration, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_subscriptionCts, cts))
                {
                    return accepted;
                }

                if (ReferenceEquals(_pendingCreateRequest, requestSnapshot))
                {
                    _pendingCreateRequest = null;
                    _isPendingCreateOutcomeUnknown = false;
                }
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            RunOnUi(() =>
            {
                if (_runOwnershipGeneration != ownershipGeneration) return;
                _statusText = accepted ? "Event monitoring stopped." : "Ready";
                _isCancelRequested = false;
                _isBusy = false;
                RefreshCommands();
            });
            return accepted;
        }
        catch (AgentApiException ex) when (
            ex.IsCreateOutcomeUnknown
            || string.Equals(ex.ErrorCode, "agent_create_outcome_unknown", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCreateRequest, requestSnapshot))
                {
                    _isPendingCreateOutcomeUnknown = true;
                }

                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            RunOnUi(() =>
            {
                if (!ReferenceEquals(_pendingCreateRequest, requestSnapshot)) return;
                _statusText = CreateOutcomeUnknownStatus(ex.ErrorCode);
                _isBusy = false;
                _isCancelRequested = false;
                RefreshCommands();
            });
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (token.IsCancellationRequested && !ReferenceEquals(_subscriptionCts, cts))
                {
                    return accepted;
                }

                if (ReferenceEquals(_pendingCreateRequest, requestSnapshot))
                {
                    _pendingCreateRequest = null;
                    _isPendingCreateOutcomeUnknown = false;
                }
            }

            RunOnUi(() =>
            {
                if (_runOwnershipGeneration != ownershipGeneration) return;
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
            createSettled.TrySetResult();
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCreateSettled, createSettled)) _pendingCreateSettled = null;
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private static CreateAgentRunRequest SnapshotCreateRequest(CreateAgentRunRequest request)
    {
        if (request.ImageAttachments is null)
        {
            return request;
        }

        var attachments = new string[request.ImageAttachments.Count];
        for (var index = 0; index < attachments.Length; index++)
        {
            attachments[index] = request.ImageAttachments[index];
        }

        return request with { ImageAttachments = Array.AsReadOnly(attachments) };
    }

    private static string CreateOutcomeUnknownStatus(string errorCode) =>
        string.Equals(errorCode, "agent_create_outcome_unknown", StringComparison.Ordinal)
            ? "Create outcome unknown: agent_create_outcome_unknown. Use Stop to reconcile and cancel."
            : $"Create outcome unknown: agent_create_outcome_unknown (last response: {errorCode}). Use Stop to reconcile and cancel.";

    private void PublishCreatedRun(
        AgentRunModel run,
        long ownershipGeneration,
        CancellationToken token,
        Action? onAccepted)
    {
        RunOnUi(() =>
        {
            lock (_gate)
            {
                if (_runOwnershipGeneration != ownershipGeneration || token.IsCancellationRequested) return;
            }

            CurrentRun = run;
            _waitKind = WaitKindFor(run);
            _statusText = $"Running: {run.RunId}";
            RefreshCommands();
            onAccepted?.Invoke();
        });
    }

    private async Task CancelLateCreatedRunAsync(string runId, CancellationTokenSource createCts)
    {
        long cleanupGeneration;
        try
        {
            await _client.CancelAsync(runId).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_subscriptionCts, createCts)) return;
                cleanupGeneration = _runOwnershipGeneration;
                _currentRunId = null;
                _subscriptionCts = null;
                _isCancelRequested = false;
            }

            RunOnUi(() =>
            {
                if (_runOwnershipGeneration != cleanupGeneration) return;
                _isBusy = false;
                _isCancelRequested = false;
                _statusText = "Ready";
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_subscriptionCts, createCts)) return;
                cleanupGeneration = _runOwnershipGeneration;
                _currentRunId = runId;
                _isCancelRequested = false;
            }

            RunOnUi(() =>
            {
                if (_runOwnershipGeneration != cleanupGeneration) return;
                _statusText = $"Could not cancel the run after the session changed: {ex.Message}";
                RefreshCommands();
            });
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
        long ownershipGeneration;
        lock (_gate)
        {
            if (_currentRunId is not null || _isBusy || cts.IsCancellationRequested)
            {
                cts.Dispose();
                return Task.FromResult(false);
            }
            _isBusy = true;
            _subscriptionCts = cts;
            ownershipGeneration = ++_runOwnershipGeneration;
            _deltaDeduplicator.Reset();
            _streamTerminalStatus = null;
            // Hand the run id over before the first UI frame: the delta/activity
            // handlers read it on the SSE thread, and a UI-queued assignment lets
            // the opening frames arrive while it is still null and be dropped.
            _currentRunId = run.RunId;
        }

        var token = cts.Token;

        RunOnUi(() =>
        {
            if (_runOwnershipGeneration != ownershipGeneration) return;
            OnPropertyChanged(nameof(IsBusy));
            _isCancelRequested = false;
            _waitKind = WaitKindFor(run);
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            PendingInputId = null;
            PendingInputQuestion = null;
            PendingInputOptions = Array.Empty<string>();
            Events.Clear();

            CurrentRun = run;
            RunAdopted?.Invoke(this, new AgentRunAdoptedEventArgs(run));
            _statusText = AdoptedStatusText(run);
            RefreshCommands();
        });

        _ = FollowAdoptedRunAsync(run.RunId, run.LastSequence, ownershipGeneration, token, cts);
        return Task.FromResult(true);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CreateAgentRunRequest? pendingCreate;
        lock (_gate)
        {
            pendingCreate = _currentRunId is null
                && _subscriptionCts is null
                && _isPendingCreateOutcomeUnknown
                && !_isBusy
                    ? _pendingCreateRequest
                    : null;
        }

        if (pendingCreate is not null)
        {
            await ReconcileAndCancelPendingCreateAsync(pendingCreate, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? runId = BeginRunTeardown();
        if (runId is null)
        {
            return;
        }

        long ownershipGeneration;
        lock (_gate) ownershipGeneration = _runOwnershipGeneration;
        RunOnUi(() =>
        {
            if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
            _statusText = "Cancelling run...";
            RefreshCommands();
        });

        try
        {
            await _client.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
            ReleaseCurrentRun(runId, "Cancellation request sent.", ownershipGeneration);
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
                _statusText = $"Cancel error: {ex.Message}";
                _isBusy = true;
                _isCancelRequested = false;
                RefreshCommands();
            });
        }
    }

    private async Task ReconcileAndCancelPendingCreateAsync(
        CreateAgentRunRequest pendingCreate,
        CancellationToken cancellationToken)
    {
        if (!TryBeginPendingCreateReconciliation(pendingCreate, out var ownershipGeneration)) return;
        SetPendingCreateReconciliationStatus("Checking whether the run was created...");

        try
        {
            var run = await _client.CreateRunAsync(pendingCreate, cancellationToken).ConfigureAwait(false);
            if (!TryRememberReconciledRun(pendingCreate, run)) return;
            PublishReconciledRun(run, ownershipGeneration);
            await _client.CancelAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (!OwnsReconciliation(ownershipGeneration)) return;
            ReleaseCurrentRun(run.RunId, "Cancellation request sent.");
        }
        catch (Exception ex)
        {
            SetPendingCreateReconciliationFailure(pendingCreate, ownershipGeneration, ex);
        }
    }

    private bool TryBeginPendingCreateReconciliation(
        CreateAgentRunRequest pendingCreate,
        out long ownershipGeneration)
    {
        lock (_gate)
        {
            if (_isBusy
                || !_isPendingCreateOutcomeUnknown
                || !ReferenceEquals(_pendingCreateRequest, pendingCreate))
            {
                ownershipGeneration = 0;
                return false;
            }

            _isBusy = true;
            _isCancelRequested = true;
            ownershipGeneration = ++_runOwnershipGeneration;
            return true;
        }
    }

    private void SetPendingCreateReconciliationStatus(string status)
    {
        RunOnUi(() =>
        {
            _statusText = status;
            RefreshCommands();
        });
    }

    private bool TryRememberReconciledRun(CreateAgentRunRequest pendingCreate, AgentRunModel run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingCreateRequest, pendingCreate)) return false;
            _pendingCreateRequest = null;
            _isPendingCreateOutcomeUnknown = false;
            _currentRunId = run.RunId;
            return true;
        }
    }

    private void PublishReconciledRun(AgentRunModel run, long ownershipGeneration)
    {
        RunOnUi(() =>
        {
            if (!OwnsReconciliation(ownershipGeneration)) return;
            CurrentRun = run;
            _waitKind = WaitKindFor(run);
            _statusText = $"Run found. Cancelling: {run.RunId}";
            RefreshCommands();
        });
    }

    private bool OwnsReconciliation(long ownershipGeneration)
    {
        lock (_gate)
        {
            return _runOwnershipGeneration == ownershipGeneration;
        }
    }

    private void SetPendingCreateReconciliationFailure(
        CreateAgentRunRequest pendingCreate,
        long ownershipGeneration,
        Exception error)
    {
        bool outcomeUnknown;
        lock (_gate)
        {
            if (_runOwnershipGeneration != ownershipGeneration) return;
            outcomeUnknown = ReferenceEquals(_pendingCreateRequest, pendingCreate);
            _isPendingCreateOutcomeUnknown = outcomeUnknown;
        }

        RunOnUi(() =>
        {
            if (!OwnsReconciliation(ownershipGeneration)) return;
            _statusText = outcomeUnknown
                ? "Create outcome unknown: agent_create_outcome_unknown. Use Stop to retry reconciliation."
                : $"Cancel error: {error.Message}";
            _isBusy = false;
            _isCancelRequested = false;
            RefreshCommands();
        });
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
    /// Leaves Host execution running and releases only this view's subscription.
    /// A pending create must settle before detaching: cancelling its HTTP request
    /// could lose the identity of a run the Host already accepted.
    /// </summary>
    public async Task StopForSessionSwitchAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        long generation;
        lock (_gate)
        {
            pending = _pendingCreateSettled?.Task ?? Task.CompletedTask;
            generation = _runOwnershipGeneration;
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_runOwnershipGeneration != generation) return;
            if (_isPendingCreateOutcomeUnknown)
                throw new InvalidOperationException("Create outcome unknown. Reconcile the pending run before leaving this session.");
        }
        await DetachCurrentRunAsync(generation).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the current run without cancelling it on the host: the follow stream is
    /// torn down, the run reference is cleared, and no cancel request is posted. The
    /// run keeps its current state server-side, so opening the session again adopts
    /// it. The async shape matches <see cref="CancelAsync"/> for a session-switch
    /// site that awaits the teardown.
    /// </summary>
    public Task DetachCurrentRunAsync()
    {
        long generation;
        lock (_gate) generation = _runOwnershipGeneration;
        return DetachCurrentRunAsync(generation);
    }

    private Task DetachCurrentRunAsync(long generation)
    {
        string? runId = BeginRunTeardown(generation);
        if (runId is null)
        {
            return Task.CompletedTask;
        }

        // The non-cancel half of CancelAsync's teardown: same clearing and same
        // idle transition, minus the POST and with copy that says the run survives.
        ReleaseCurrentRun(runId, "Run detached. Execution continues on the host.", generation + 1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts tearing the current run down: the follow stream is cancelled and the
    /// run is marked as no longer driven. Shared by cancel and detach; returns the
    /// run id being torn down, or null when there is nothing to tear down.
    /// </summary>
    private string? BeginRunTeardown(long? expectedGeneration = null)
    {
        lock (_gate)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _runOwnershipGeneration) return null;
            string? runId = _currentRunId;
            if (_subscriptionCts is not null || runId is not null)
            {
                _runOwnershipGeneration++;
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
    private void ReleaseCurrentRun(string runId, string statusText, long? ownershipGeneration = null)
    {
        RunOnUi(() =>
        {
            if (!string.Equals(_currentRunId, runId, StringComparison.Ordinal)
                || (ownershipGeneration.HasValue && _runOwnershipGeneration != ownershipGeneration.Value)) return;
            lock (_gate)
            {
                _currentRunId = null;
                _subscriptionCts = null;
                _isCancelRequested = false;
            }
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            PendingInputId = null;
            PendingInputQuestion = null;
            PendingInputOptions = Array.Empty<string>();
            CurrentRun = null;

            _statusText = statusText;
            _isBusy = false;
            RefreshCommands();
        });
    }

    /// <summary>
    /// Typed Agent command for one run, e.g. "/tasks" or "/tasks kill
    /// &lt;execution_id&gt;". The Host answers with bounded metadata.
    /// </summary>
    public Task<DispatchAgentCommandResponse> DispatchCommandAsync(
        string runId,
        string text,
        CancellationToken cancellationToken = default)
        => _client.DispatchCommandAsync(runId, text, cancellationToken);

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
        await SubmitApprovalAsync(AgentApprovalOperation.AllowOnce, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves an external-directory request for the whole chat session. Only
    /// offered when the pending ask reason is external_directory_required; the
    /// server fails anything else closed and the grant dies with the session.
    /// </summary>
    public async Task ApproveSessionDirectoryAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.AllowSessionDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves once and remembers the exact intent as a project rule. Offered
    /// for known non-external asks; the server fails external-directory asks
    /// closed for this scope and unknown reasons stay one-time.
    /// </summary>
    public async Task ApproveAlwaysAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.AllowAlways, cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.Deny, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the Host-intercepted question the run is waiting on. The answer
    /// is posted to the same resolve surface the approval decision uses; on
    /// success the card is cleared at once, so submitting hides it even before
    /// the resolved frame arrives.
    /// </summary>
    public async Task SubmitInputAsync(string answer, CancellationToken cancellationToken = default)
    {
        var request = CaptureInputRequest();
        await _inputCoordinator.SubmitAsync(
            answer,
            request.RunId,
            request.InputId,
            _client,
            status => RunOnUi(() => _statusText = status),
            IsCurrentInputRequest,
            () => RunOnUi(() =>
            {
                if (!IsCurrentInputRequest(request.RunId!, request.InputId!)) return;
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
            }),
            RefreshAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private Task SubmitApprovalAsync(AgentApprovalOperation operation, CancellationToken cancellationToken) =>
        SubmitApprovalAsync(CaptureApprovalRequest(), operation, cancellationToken);

    private Task SubmitApprovalAsync(
        (string? RunId, string? ApprovalId) request,
        AgentApprovalOperation operation,
        CancellationToken cancellationToken) => _approvalCoordinator.SubmitAsync(
            request.RunId,
            request.ApprovalId,
            operation,
            _client,
            status => RunOnUi(() => _statusText = status),
            IsCurrentApprovalRequest,
            RefreshAsync,
            cancellationToken);

    private (string? RunId, string? ApprovalId) CaptureApprovalRequest()
    {
        lock (_gate) return (_currentRun?.RunId, _approvalCoordinator.Id);
    }

    private (string? RunId, string? InputId) CaptureInputRequest()
    {
        lock (_gate) return (_currentRun?.RunId, _inputCoordinator.Id);
    }

    private bool IsCurrentApprovalRequest(string runId, string approvalId)
    {
        lock (_gate)
        {
            return string.Equals(_currentRun?.RunId, runId, StringComparison.Ordinal)
                && string.Equals(_approvalCoordinator.Id, approvalId, StringComparison.Ordinal);
        }
    }

    private bool IsCurrentInputRequest(string runId, string inputId)
    {
        lock (_gate)
        {
            return string.Equals(_currentRun?.RunId, runId, StringComparison.Ordinal)
                && string.Equals(_inputCoordinator.Id, inputId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Re-arms the question card when a session open adopted a waiting_input run:
    /// the input.requested event sits behind the adoption cursor, so the view
    /// replays the run's durable events and hands the pending question back here.
    /// </summary>
    public void RestorePendingInput(string inputId, string? question, IReadOnlyList<string>? options)
    {
        if (string.IsNullOrWhiteSpace(inputId))
        {
            return;
        }

        RunOnUi(() =>
        {
            PendingInputId = inputId;
            PendingInputQuestion = question;
            PendingInputOptions = options ?? Array.Empty<string>();
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string? runId;
        long ownershipGeneration;
        lock (_gate)
        {
            runId = _currentRunId;
            ownershipGeneration = _runOwnershipGeneration;
        }
        if (runId is null)
        {
            return;
        }

        var run = await _client.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration)
                || !string.Equals(_currentRunId, run.RunId, StringComparison.Ordinal))
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

            if (!string.Equals(run.Status, "waiting_input", StringComparison.Ordinal))
            {
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
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



    /// <summary>
    /// Reads the payload a durable event points at. A fetch failure is bounded
    /// and observational: the caller keeps its fallback for this event instead
    /// of the stream stopping, and only the failure type is traced.
    /// </summary>
    private async Task<string?> FetchPayloadContentAsync(AgentEventModel model)
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
                .GetPayloadAsync(model.RunId, model.PayloadRef!, fetchToken)
                .ConfigureAwait(false);
            return payload.Content;
        }
        catch (OperationCanceledException)
        {
            // Subscription torn down; the caller keeps its fallback for this event.
            return null;
        }
        catch (Exception exception)
        {
            GuiDiagnosticWriter.AppendTraceLine(
                $"Agent payload fetch failed: {exception.GetType().Name}");
            return null;
        }
    }

    private async Task OnEventAsync(AgentEventModel model, string runId, long ownershipGeneration)
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
            usage = AgentModelUsage.Parse(await FetchPayloadContentAsync(model).ConfigureAwait(false));
        }

        AgentInputRequest.PendingInput? inputRequest = null;
        if (string.Equals(model.EventType, "input.requested", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(model.PayloadRef))
        {
            inputRequest = AgentInputRequest.Parse(await FetchPayloadContentAsync(model).ConfigureAwait(false));
        }

        if (string.Equals(model.EventType, "final_snapshot", StringComparison.Ordinal)
            && IsTerminalStatus(model.Status))
        {
            lock (_gate)
            {
                if (OwnsFeedLocked(runId, ownershipGeneration)) _streamTerminalStatus = model.Status;
            }
        }

        RunOnUi(() =>
        {
            lock (_gate)
            {
                if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                {
                    return;
                }
            }

            if (Events.Count > 200)
            {
                Events.RemoveAt(0);
            }

            Events.Add(model);
            if (!model.IsTransient && string.Equals(model.EventType, "context.compacted", StringComparison.Ordinal))
            {
                ContextCompacted?.Invoke(
                    this,
                    new AgentRunContextCompactedEventArgs(model.RunId, model.SessionId, model.Sequence, ownershipGeneration));
            }
            if (!string.IsNullOrEmpty(model.SafeSummary)
                && !IsApprovalRequestWithPanel(model)
                && !IsInputRequestEvent(model))
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

            if (!model.IsTransient
                && model.EventType == "input.requested")
            {
                if (inputRequest is not null)
                {
                    PendingInputId = inputRequest.InputId;
                    PendingInputQuestion = inputRequest.Question;
                    PendingInputOptions = inputRequest.Options;
                }
                else
                {
                    // The payload could not be read; the run still waits, so say
                    // what it waits on instead of leaving the panel blank.
                    _statusText = "Waiting for input.";
                }
            }
            else if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, "input_resolved", StringComparison.Ordinal))
            {
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
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
                    new AgentUsageReceivedEventArgs(model.RunId, model.SessionId, usage, model.Sequence));
            }

            RefreshCommands();
        });

        await Task.CompletedTask;
    }

    private Task OnActivityAsync(AgentActivityModel model, string runId, long ownershipGeneration)
    {
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
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
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                if (model.Kind == AgentActivityKind.Terminal && !string.IsNullOrWhiteSpace(model.Detail))
                {
                    PendingApprovalCommand = model.Detail;
                }

                ActivityReceived?.Invoke(this, new AgentRunActivityEventArgs(runId, ownershipGeneration, model));
            }
            catch
            {
                // A visual activity subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnSummaryAsync(AgentRunSummaryModel model, string runId, long ownershipGeneration)
    {
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
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
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                SummaryReceived?.Invoke(this, new AgentRunSummaryEventArgs(runId, ownershipGeneration, model));
            }
            catch
            {
                // A visual summary subscriber is observational and must not fail the
                // Agent SSE subscription or durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnAssistantDeltaAsync(AgentAssistantDeltaModel model, string runId, long ownershipGeneration)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested || _currentRunId is null)
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

        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy
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

                    if (!_deltaDeduplicator.TryAcceptAssistant(runId, model.ModelStepId, model.TransientSequence))
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

    private Task OnToolOutputAsync(AgentToolOutputModel model, string runId, long ownershipGeneration)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested || _currentRunId is null)
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
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (!_deltaDeduplicator.TryAcceptToolOutput(runId, model.ExecutionId, model.TransientSequence))
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
        long ownershipGeneration,
        CancellationToken token,
        Action? onFrame = null)
    {
        await _client.SubscribeEventsAsync(
                runId,
                afterSequence,
                model =>
                {
                    onFrame?.Invoke();
                    return OnEventAsync(model, runId, ownershipGeneration);
                },
                token,
                model =>
                {
                    onFrame?.Invoke();
                    return OnActivityAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnSummaryAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnAssistantDeltaAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnToolOutputAsync(model, runId, ownershipGeneration);
                })
            .ConfigureAwait(false);
        // The SSE stream ends after the terminal snapshot, but the create response
        // may be stale (or the stream may have disconnected). Refresh the durable
        // run before changing command state so Cancel is never left enabled for a
        // completed run and a disconnect is not reported as success.
        if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
        try
        {
            await RefreshAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested && TryApplyTerminalStreamSnapshot(runId, ownershipGeneration))
        {
            // The SSE stream already delivered the authoritative terminal
            // snapshot. A follow-up GET can race backend shutdown/restart; do
            // not turn a completed reply into a false client-side failure.
        }

        RunOnUi(() =>
        {
            if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
            var terminal = IsTerminalStatus(CurrentRun?.Status);
            if (terminal)
            {
                _currentRunId = null;
                _waitKind = null;
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
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
        long ownershipGeneration,
        CancellationToken token,
        CancellationTokenSource cts)
    {
        var sawFrame = false;
        try
        {
            await FollowRunStreamAsync(runId, afterSequence, ownershipGeneration, token, () => sawFrame = true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RunOnUi(() =>
            {
                if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
                if (!sawFrame) ReleaseCurrentRun(runId, "Event monitoring stopped.", ownershipGeneration);
                else
                {
                    _statusText = "Event monitoring stopped.";
                    _isBusy = false;
                    RefreshCommands();
                }
            });
        }
        catch (Exception)
        {
            RunOnUi(() =>
            {
                if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
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

    private bool TryApplyTerminalStreamSnapshot(string runId, long ownershipGeneration)
    {
        string? status;
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration)) return false;
            status = _streamTerminalStatus;
        }

        if (!IsTerminalStatus(status) || string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        RunOnUi(() =>
        {
            if (!IsCurrentFeedOwner(runId, ownershipGeneration)
                || CurrentRun is not { } current)
            {
                return;
            }

            CurrentRun = current with { Status = status! };
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            PendingInputId = null;
            PendingInputQuestion = null;
            PendingInputOptions = Array.Empty<string>();
            RefreshCommands();
        });
        return true;
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
