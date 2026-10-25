using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

public sealed partial class AgentRunController
{
    private string? _currentRunId;
    private CreateAgentRunRequest? _pendingCreateRequest;
    private TaskCompletionSource? _pendingCreateSettled;
    private long _runOwnershipGeneration;
    private bool _isPendingCreateOutcomeUnknown;
    private CancellationTokenSource? _subscriptionCts;
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
                _feedState.ResetForRun();
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
            _feedState.ResetForRun();
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
}
