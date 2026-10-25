using System.Collections.Concurrent;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness;

/// <summary>
/// The runtime-owned cleanup one deleted session's non-terminal runs need:
/// stop each run's in-flight tools through its per-run coordinator, then drop
/// the registration along with its Broker gateway and retained approvals.
/// </summary>
public interface IAgentRuntimeRunCleanup
{
    Task CancelAndRemoveRunAsync(RunId runId, CancellationToken cancellationToken);
}

/// <summary>
/// Drives the headless coding loop: delivers conversation-outbox transitions through the
/// dispatcher, then runs model/tool steps for every running run it holds a workspace
/// root for. Runs stop being driven when they block on approval/history or complete.
/// </summary>
public sealed class AgentRuntimeLoopService : BackgroundService, RuAgent.Hosting.IHostIdleProbe, IAgentRuntimeRunCleanup
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The interval used when no registered run can progress on its own: none is
    /// registered, or every one waits for a client. This is a desktop agent that stays
    /// open for hours: four wakeups per second with database round trips each, while
    /// the user is doing something else, is pure idle cost. New runs and client answers
    /// wake the loop through <see cref="AgentRuntimeWakeSignal"/>, so the slower poll
    /// is only the fallback for changes nothing signals.
    /// </summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Runs driven at once. A slot is held for a whole drive pass, including a long
    /// terminal command, so four slots let four builds stall every other session and
    /// the subagents of a parent. Model calls keep their own, smaller bound
    /// (<c>RU_MAX_CONCURRENT_MODEL_STEPS</c>); this bound only stops a burst of runs from
    /// exhausting the host.
    /// </summary>
    internal const int MaxConcurrentRuns = 16;

    private readonly AgentConversationOutboxDispatcher _dispatcher;
    private readonly IHarnessEventStore _store;
    private readonly IAgentRuntimeCoordinatorFactory _coordinatorFactory;
    private readonly IAgentRuntimeRootRegistry _roots;
    private readonly ILogger<AgentRuntimeLoopService> _logger;
    private readonly AgentRunSummaryLivePublisher? _summaryPublisher;
    private readonly WorkspaceExternalChangeMonitor? _workspaceChangeMonitor;
    private readonly WorkspaceCoordinatorStore _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector _workspaceJournalProtector;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workerGate = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly Dictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _workerCancellation = new(StringComparer.Ordinal);
    private readonly object _workerLock = new();
    private readonly AgentRuntimeParkingState _parkingState = new();
    private readonly AgentRuntimeRunLifecycle _runLifecycle;
    private readonly WorkspaceWatchRegistry? _workspaceWatches;
    private readonly Broker.IToolGatewayFactory _brokerGateways;
    private readonly RetainedApprovalRehydrator? _retainedApprovalRehydrator;
    private readonly RetainedInputRehydrator? _retainedInputRehydrator;
    private readonly HarnessRecoveryScanner _recoveryScanner;
    private readonly Subagents.SubagentScheduler? _subagents;
    private readonly Subagents.SubagentWaitCoordinator? _subagentWaits;
    private readonly Subagents.SubagentContinuationCoordinator? _subagentContinuations;
    private readonly Subagents.SubagentUserApplyCoordinator? _userApplies;
    private readonly Subagents.SubagentLivePublisher? _subagentLive;
    private readonly AgentRuntimeWakeSignal? _wake;
    private readonly Hooks.RunLifecycleHooks? _lifecycleHooks;

    public AgentRuntimeLoopService(
        AgentConversationOutboxDispatcher dispatcher,
        IHarnessEventStore store,
        IAgentRuntimeCoordinatorFactory coordinatorFactory,
        IAgentRuntimeRootRegistry roots,
        ILogger<AgentRuntimeLoopService> logger,
        WorkspaceCoordinatorStore workspaceCoordinatorStore,
        WorkspaceJournalProtector workspaceJournalProtector,
        Broker.IToolGatewayFactory brokerGateways,
        AgentRunSummaryLivePublisher? summaryPublisher = null,
        WorkspaceExternalChangeMonitor? workspaceChangeMonitor = null,
        RetainedApprovalRehydrator? retainedApprovalRehydrator = null,
        RetainedInputRehydrator? retainedInputRehydrator = null,
        Subagents.SubagentScheduler? subagents = null,
        Subagents.SubagentWaitCoordinator? subagentWaits = null,
        Subagents.SubagentContinuationCoordinator? subagentContinuations = null,
        Subagents.SubagentUserApplyCoordinator? userApplies = null,
        Subagents.SubagentLivePublisher? subagentLive = null,
        AgentRuntimeWakeSignal? wake = null,
        Hooks.RunLifecycleHooks? lifecycleHooks = null)
    {
        _wake = wake;
        _lifecycleHooks = lifecycleHooks;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinatorFactory = coordinatorFactory ?? throw new ArgumentNullException(nameof(coordinatorFactory));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _summaryPublisher = summaryPublisher;
        _workspaceChangeMonitor = workspaceChangeMonitor;
        _workspaceWatches = workspaceChangeMonitor is null ? null : new WorkspaceWatchRegistry(workspaceChangeMonitor);
        _workspaceCoordinatorStore = workspaceCoordinatorStore ?? throw new ArgumentNullException(nameof(workspaceCoordinatorStore));
        _workspaceJournalProtector = workspaceJournalProtector ?? throw new ArgumentNullException(nameof(workspaceJournalProtector));
        _brokerGateways = brokerGateways ?? throw new ArgumentNullException(nameof(brokerGateways));
        _retainedApprovalRehydrator = retainedApprovalRehydrator;
        _retainedInputRehydrator = retainedInputRehydrator;
        _recoveryScanner = new HarnessRecoveryScanner(_store, new SystemTimeProvider());
        _subagents = subagents;
        _subagentWaits = subagentWaits;
        _subagentContinuations = subagentContinuations;
        _userApplies = userApplies;
        _subagentLive = subagentLive;
        _runLifecycle = new(_roots, _brokerGateways, _coordinatorFactory, _parkingState, _subagentLive);
    }

    /// <summary>Registered runs the loop is driving; surfaced to the idle-shutdown probe.</summary>
    public int ActiveWorkCount { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var nextDelay = IdlePollInterval;
                try
                {
                    var hasActiveRuns = await StepOnceAsync(stoppingToken, waitForWorkers: false).ConfigureAwait(false);
                    nextDelay = hasActiveRuns ? PollInterval : IdlePollInterval;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "agent runtime loop iteration failed");
                }

                if (_wake is null) await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false);
                else await _wake.WaitAsync(nextDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Task[] workers;
            lock (_workerLock) workers = _workers.Values.ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            lock (_workerLock)
            {
                foreach (var source in _workerCancellation.Values) source.Dispose();
                _workerCancellation.Clear();
                _workers.Clear();
            }
        }
    }

    /// <summary>
    /// Runs one iteration. Returns true when a registered run may progress without a
    /// client request, which is the signal the loop uses to choose its next poll interval.
    /// </summary>
    internal async Task<bool> StepOnceAsync(CancellationToken cancellationToken, bool waitForWorkers = true)
    {
        // Outbox delivery first so history transitions land before any new model step.
        var dispatch = await _dispatcher.DispatchOnceAsync(cancellationToken).ConfigureAwait(false);
        if (_subagentContinuations is not null) await _subagentContinuations.TickAsync(cancellationToken).ConfigureAwait(false);
        if (_subagents is not null) await _subagents.TickAsync(this, cancellationToken).ConfigureAwait(false);
        if (_subagentWaits is not null) await _subagentWaits.TickAsync(cancellationToken).ConfigureAwait(false);

        // Drive every registered running run on every iteration: state can become
        // runnable without an outbox write too (e.g. approval_resolved after a V1
        // approve), so delivery alone is not a sufficient trigger.
        _ = dispatch;
        var registrations = _roots.Snapshot();
        if (_subagentLive is not null)
            foreach (var registration in registrations)
                await _subagentLive.PublishAsync(new(registration.RunId), cancellationToken).ConfigureAwait(false);
        // Published for the managed host's idle-shutdown policy: a registered run (even
        // parked) keeps the host alive so a detached turn keeps progressing.
        ActiveWorkCount = registrations.Count;
        // A long-running terminal command must not stall every other run. Keep a
        // bounded degree of concurrency so a burst of runs cannot exhaust the host
        // with model/process work while still allowing independent runs to progress.
        var tasks = new List<Task>();
        lock (_workerLock)
        {
            foreach (var completed in _workers.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
            {
                _workers.Remove(completed);
                _workerCancellation[completed].Dispose();
                _workerCancellation.Remove(completed);
            }
            foreach (var registration in registrations)
            {
                if (!_workers.TryGetValue(registration.RunId, out var worker))
                {
                    var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var workerToken = source.Token;
                    _workerCancellation.Add(registration.RunId, source);
                    worker = Task.Run(async () =>
                    {
                        try
                        {
                            if (!_workerGate.Wait(0, CancellationToken.None))
                            {
                                _logger.LogInformation(
                                    "run {RunId} is waiting for one of {Slots} runtime slots",
                                    registration.RunId,
                                    MaxConcurrentRuns);
                                await _workerGate.WaitAsync(workerToken).ConfigureAwait(false);
                            }
                            try
                            {
                                var runId = new RunId(registration.RunId);
                                // A queued run may have been removed while another worker held the slot.
                                if (!_roots.TryGetRoot(runId, out _)) return;
                                _roots.TryGetPolicySnapshot(runId, out var policySnapshot);
                                await DriveRunIfRunningAsync(registration.RunId, registration.WorkspaceRoot,
                                    policySnapshot, workerToken).ConfigureAwait(false);
                            }
                            finally { _workerGate.Release(); }
                        }
                        catch (OperationCanceledException) when (workerToken.IsCancellationRequested) { }
                        catch (Exception error) { _logger.LogWarning(error, "agent run worker failed for {RunId}", registration.RunId); }
                    }, CancellationToken.None);
                    _workers.Add(registration.RunId, worker);
                }
                tasks.Add(worker);
            }
        }

        if (waitForWorkers) await Task.WhenAll(tasks).ConfigureAwait(false);
        // Without a wake signal nothing announces a client's answer, so keep the
        // active rate for every registered run.
        return _wake is null
            ? registrations.Count > 0
            : registrations.Any(registration => !_parkingState.IsWaitingOnClient(registration.RunId));
    }

    private async Task DriveRunIfRunningAsync(
        string runIdValue,
        string workspaceRoot,
        PolicySnapshot? policySnapshot,
        CancellationToken cancellationToken)
    {
        if (!_inFlight.TryAdd(runIdValue, 0))
        {
            return;
        }

        try
        {
            var run = await LoadRunAsync(runIdValue, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                // The run row is gone (session deletion or retention). Drop the
                // registration here or it would keep the host alive forever and
                // hold a Broker gateway for a run nothing can ever drive again.
                await RemoveRunRegistrationAsync(new RunId(runIdValue)).ConfigureAwait(false);
                return;
            }

            if (run.State.IsTerminal())
            {
                if (_userApplies is not null) await _userApplies.DriveAsync(run, cancellationToken).ConfigureAwait(false);
                await PublishRunFinishedAsync(run, cancellationToken).ConfigureAwait(false);
                await RemoveRunRegistrationAsync(new RunId(runIdValue)).ConfigureAwait(false);
                return;
            }

            if (_userApplies is not null && await _userApplies.DriveAsync(run, cancellationToken).ConfigureAwait(false)) return;

            if (HarnessRecoveryScanner.IsExpiredWorkspaceTrustWait(run, DateTimeOffset.UtcNow))
            {
                // The trust challenge is past its lifetime, so no client can resolve it
                // any more. The startup sweep used to be the only path that noticed, which
                // left the run parked — and its session's active-run slot held — for as
                // long as the host stayed up.
                await ReleaseExpiredWorkspaceTrustAsync(runIdValue, run, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (HarnessRecoveryScanner.IsExpiredApprovalWait(run, DateTimeOffset.UtcNow))
            {
                // The durable approval is past its lifetime, so no client can
                // resolve it any more. Fail the run now instead of leaving it
                // parked while it still holds its session's active-run slot.
                await ReleaseExpiredApprovalAsync(runIdValue, run, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (run.State != HarnessRunState.Running
                && run.State != HarnessRunState.WaitingApproval)
            {
                _parkingState.ObserveClientWaitState(runIdValue, run.State);
                return;
            }

            // A run parked on an approval card has nothing to do until the card is
            // resolved, and resolving it commits a new run version. Driving it anyway
            // rebuilt the coordinator and gateway, rehydrated the approval and rewrote the
            // workspace lease on every tick for as long as the card stayed open. The
            // expiry checks above still run each tick.
            if (run.State == HarnessRunState.WaitingApproval
                && _parkingState.IsParkedApprovalVersion(runIdValue, run.Version))
            {
                _parkingState.MarkWaitingOnClient(runIdValue);
                return;
            }

            _parkingState.MarkRunnable(runIdValue);

            // The run this pass already read is handed to the drive, which would otherwise
            // read the same row again a few lines later. The loop runs every 250 ms per
            // registered run, so that duplicate read is the loop's steady-state database
            // cost: two reads per tick where one is enough.
            await DriveRunAsync(runIdValue, workspaceRoot, policySnapshot, run, cancellationToken).ConfigureAwait(false);
            var driven = await LoadRunAsync(runIdValue, cancellationToken).ConfigureAwait(false);
            if (driven is { State: HarnessRunState.WaitingApproval })
            {
                _parkingState.RecordParkedApprovalVersion(runIdValue, driven.Version);
            }

            // A pass that moved the run on usually leaves outbox work (the history row
            // a parked or finished run waits for), which only the loop delivers. A pass
            // that changed nothing, such as one refused a busy workspace lease, must
            // not wake the loop or it would spin on the same refusal.
            if (driven is not null && driven.Version != run.Version)
            {
                _wake?.Wake();
            }
        }
        finally
        {
            _inFlight.TryRemove(runIdValue, out _);
        }
    }

    /// <summary>
    /// Session deletion's runtime half: stop the run's in-flight tools through
    /// its per-run coordinator, then release the registration, Broker gateway,
    /// and retained approvals. Safe when the run holds no registration.
    /// </summary>
    public async Task CancelAndRemoveRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        lock (_workerLock)
        {
            if (_workerCancellation.TryGetValue(runId.Value, out var source)) source.Cancel();
        }
        await _roots.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);
        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
    }

    private async Task RemoveRunRegistrationAsync(RunId runId)
    {
        if (_userApplies is not null) await _userApplies.CancelPendingAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (_subagents is not null)
            await _subagents.CancelChildrenBeforeReleaseAsync(runId, this, CancellationToken.None).ConfigureAwait(false);
        await _runLifecycle.ReleaseRegistrationAsync(runId).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a run whose workspace-trust challenge has expired and drops its
    /// registration. Internal so the recovery tests can drive the loop's own sweep
    /// without standing up the background service.
    /// </summary>
    internal async Task ReleaseExpiredWorkspaceTrustAsync(
        string runIdValue,
        AgentRun waitingRun,
        CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        RecoveredRunSummary? released;
        try
        {
            released = await _recoveryScanner
                .CancelExpiredWorkspaceTrustAsync(waitingRun, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "failed to release expired workspace trust of run {RunId}", runIdValue);
            return;
        }

        if (released is null)
        {
            return;
        }

        _logger.LogWarning("run {RunId} cancelled because its workspace trust challenge expired", runIdValue);
        await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
    }

    private async Task ReleaseExpiredApprovalAsync(
        string runIdValue,
        AgentRun waitingRun,
        CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        RecoveredRunSummary? released;
        try
        {
            released = await _recoveryScanner
                .FailExpiredApprovalAsync(waitingRun, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "failed to release expired approval of run {RunId}", runIdValue);
            return;
        }

        if (released is null)
        {
            return;
        }

        _logger.LogWarning("run {RunId} failed because its approval expired", runIdValue);
        await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
    }

    internal async Task<(bool Continue, RetainedApprovalStep? Retained)> ReconcileApprovalRefreshRaceAsync(
        string runIdValue,
        RunId runId,
        AgentRunCoordinator coordinator,
        RetainedApprovalRehydrator rehydrator,
        CancellationToken cancellationToken)
    {
        var racedRun = await LoadRunAsync(runIdValue, cancellationToken).ConfigureAwait(false);
        if (racedRun is null)
        {
            return (false, null);
        }

        if (racedRun.State.IsTerminal())
        {
            await PublishRunFinishedAsync(racedRun, cancellationToken).ConfigureAwait(false);
            await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
            return (false, null);
        }

        if (racedRun.State != HarnessRunState.Running)
        {
            return (false, null);
        }

        var retained = await rehydrator
            .RehydrateAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (retained is not null)
        {
            coordinator.RestoreRetainedApproval(runId, retained);
        }

        return (true, retained);
    }

    private async Task<AgentRun?> LoadRunAsync(string runIdValue, CancellationToken cancellationToken) =>
        await _store.GetRunAsync(new RunId(runIdValue), cancellationToken).ConfigureAwait(false);

    private async Task DriveRunAsync(
        string runIdValue,
        string workspaceRoot,
        PolicySnapshot? policySnapshot,
        AgentRun workspaceRun,
        CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        AgentRunCoordinator? coordinator = null;
        IDisposable? workspaceWatcher = null;
        WorkspaceRunMembershipRecord? membership = null;
        WorkspaceMembershipHeartbeat? membershipHeartbeat = null;
        try
        {
            _summaryPublisher?.RegisterWorkspace(runId, workspaceRoot);

            {
                var workspace = await _workspaceCoordinatorStore.GetOrCreateAsync(workspaceRun.WorkspaceId, cancellationToken).ConfigureAwait(false);
                var leaseDuration = TimeSpan.FromSeconds(workspace.LeaseTtlSeconds);
                try
                {
                    membership = await _workspaceCoordinatorStore.AcquireRunMembershipAsync(
                        workspaceRun.WorkspaceId,
                        runId,
                        "ru_peer",
                        DateTimeOffset.UtcNow,
                        leaseDuration,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessStoreException exception) when (exception.Code == "workspace_busy")
                {
                    _logger.LogInformation("run {RunId} is waiting for a workspace membership lease", runIdValue);
                    return;
                }

                membershipHeartbeat = WorkspaceMembershipHeartbeat.Start(
                    token => _workspaceCoordinatorStore.RenewRunMembershipAsync(
                        workspaceRun.WorkspaceId,
                        runId,
                        DateTimeOffset.UtcNow,
                        leaseDuration,
                        token),
                    leaseDuration,
                    cancellationToken);
            }

            if (_workspaceWatches is not null)
            {
                workspaceWatcher = _workspaceWatches.Acquire(
                    workspaceRoot,
                    change => PersistExternalChangeAsync(workspaceRun.WorkspaceId, workspaceRoot, change, CancellationToken.None));
            }
            // One allow is remembered for the session: materialize the exact
            // approved intents into this run's snapshot for every drive pass,
            // so a later identical call evaluates as allowed instead of asking
            // again. The ledger is in-process and dies with the session or a
            // host restart; the durable run identity keeps the snapshot it was
            // created with.
            var effectivePolicySnapshot = SessionApprovalLedger.Apply(
                policySnapshot,
                workspaceRun.SessionId.Value,
                DateTimeOffset.UtcNow);
            coordinator = await _coordinatorFactory.CreateAsync(workspaceRoot, effectivePolicySnapshot, runId, workspaceRun.SessionId.Value, cancellationToken).ConfigureAwait(false);
            _roots.RegisterCoordinator(runId, coordinator);

            if (policySnapshot is null)
            {
                await coordinator.FailAsync(runId, "policy_snapshot_unavailable", cancellationToken).ConfigureAwait(false);
                await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var rehydrator = _retainedApprovalRehydrator;
            RetainedApprovalStep? retained = null;
            if (rehydrator is not null)
            {
                retained = await rehydrator
                    .RehydrateAsync(runId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (workspaceRun.State == HarnessRunState.WaitingApproval)
            {
                if (rehydrator is null || retained is null)
                {
                    await coordinator.FailAsync(
                            runId,
                            "retained_approval_recovery_failed",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                    await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                    return;
                }

                if (_parkingState.IsRecoveredApproval(runIdValue, retained.ApprovalId.Value))
                {
                    return;
                }

                coordinator.RestoreRetainedApproval(runId, retained);
                var refreshed = await coordinator
                    .TryRefreshRetainedApprovalAsync(runId, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed is null)
                {
                    var race = await ReconcileApprovalRefreshRaceAsync(
                            runIdValue,
                            runId,
                            coordinator,
                            rehydrator,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!race.Continue)
                    {
                        return;
                    }

                    retained = race.Retained;
                }
                else
                {
                    if (refreshed.Run.State.IsTerminal())
                    {
                        await PublishRunFinishedAsync(refreshed.Run, cancellationToken).ConfigureAwait(false);
                        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                        return;
                    }

                    if (refreshed.ErrorCode != "approval_required")
                    {
                        await coordinator.FailAsync(
                                runId,
                                "retained_approval_recovery_failed",
                                cancellationToken)
                            .ConfigureAwait(false);
                        await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                        return;
                    }

                    var current = await rehydrator
                        .RehydrateAsync(runId, cancellationToken)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        await coordinator.FailAsync(
                                runId,
                                "retained_approval_recovery_failed",
                                cancellationToken)
                            .ConfigureAwait(false);
                        await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                        return;
                    }

                    _parkingState.RecordRecoveredApproval(runIdValue, current.ApprovalId.Value);
                    return;
                }
            }

            if (retained is not null)
            {
                coordinator.RestoreRetainedApproval(runId, retained);
            }

            _parkingState.ClearRecoveredApproval(runIdValue);
            // A previous drive may have parked proposals in an approval wait.
            // Resume them before the model steps: the receipts land in durable
            // state, so the next model step continues naturally. Null or stale
            // means nothing parked; stepping proceeds unchanged.
            var resumed = await coordinator
                .TryResumeRetainedApprovalAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (resumed is not null
                && (resumed.Run.State != HarnessRunState.Running
                    || resumed.ErrorCode is not null))
            {
                if (resumed.Run.State.IsTerminal())
                {
                    await PublishRunFinishedAsync(resumed.Run, cancellationToken).ConfigureAwait(false);
                    await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                }

                return;
            }

            // A step parked on ask_user left proposals the answer route did not
            // execute. Rebuild them from durable state and resume; a null result
            // means the asked call was the only proposal, so stepping proceeds.
            if (_retainedInputRehydrator is not null)
            {
                var retainedInput = await _retainedInputRehydrator
                    .RehydrateAsync(runId, cancellationToken)
                    .ConfigureAwait(false);
                if (retainedInput is not null)
                {
                    var resumedInput = await coordinator
                        .TryResumeRetainedInputAsync(runId, retainedInput, cancellationToken)
                        .ConfigureAwait(false);
                    if (resumedInput is not null
                        && (resumedInput.Run.State != HarnessRunState.Running
                            || resumedInput.ErrorCode is not null))
                    {
                        if (resumedInput.Run.State.IsTerminal())
                        {
                            await PublishRunFinishedAsync(resumedInput.Run, cancellationToken).ConfigureAwait(false);
                            await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                        }

                        return;
                    }
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var stepTask = coordinator.RunOneStepAsync(
                    runId,
                    stepCancellation.Token);
                if (membershipHeartbeat is not null)
                {
                    var completed = await Task.WhenAny(
                            stepTask,
                            membershipHeartbeat.Lost)
                        .ConfigureAwait(false);
                    if (completed == membershipHeartbeat.Lost
                        || membershipHeartbeat.Lost.IsCompleted)
                    {
                        await stepCancellation.CancelAsync().ConfigureAwait(false);
                        try
                        {
                            await stepTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stepCancellation.IsCancellationRequested)
                        {
                        }

                        await coordinator.FailAsync(runId, "workspace_membership_expired", cancellationToken).ConfigureAwait(false);
                        await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                        await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                        break;
                    }
                }

                var result = await stepTask.ConfigureAwait(false);
                if (result.Run.State == HarnessRunState.WaitingApproval
                    && result.ErrorCode == "approval_required"
                    && rehydrator is not null)
                {
                    var current = await rehydrator
                        .RehydrateAsync(runId, cancellationToken)
                        .ConfigureAwait(false);
                    if (current is not null)
                    {
                        _parkingState.RecordRecoveredApproval(runIdValue, current.ApprovalId.Value);
                    }
                }

                if (result.Run.State == HarnessRunState.Running && result.ErrorCode is null)
                {
                    if (_subagents?.ShouldYield(runId) == true) break;
                    continue;
                }

                if (result.ErrorCode is not null)
                {
                    _logger.LogInformation("Run {RunId} stopped with {ErrorCode} in {RunState}",
                        runIdValue, result.ErrorCode, result.Run.State);
                }

                if (result.Run.State.IsTerminal())
                {
                    await PublishRunFinishedAsync(result.Run, cancellationToken).ConfigureAwait(false);
                    await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                }

                break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "run {RunId} failed during runtime drive", runIdValue);
            if (coordinator is null)
            {
                // A missing/replaced workspace can make gateway construction fail
                // before a coordinator exists. Park the registration instead of
                // retrying the same bad root on every 250 ms poll.
                await RemoveRunRegistrationAsync(runId).ConfigureAwait(false);
                return;
            }

            try
            {
                var failureCode = NormalizeDriveFailureCode(error);
                await coordinator.FailAsync(runId, failureCode, cancellationToken).ConfigureAwait(false);
                await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception nested)
            {
                _logger.LogError(nested, "failed to mark run {RunId} as failed", runIdValue);
            }
        }
        finally
        {
            if (membershipHeartbeat is not null)
            {
                await membershipHeartbeat.DisposeAsync().ConfigureAwait(false);
            }

            workspaceWatcher?.Dispose();
            if (membership is not null)
            {
                try
                {
                    var run = await LoadRunAsync(runIdValue, CancellationToken.None).ConfigureAwait(false);
                    if (run is not null)
                    {
                        await _workspaceCoordinatorStore.ReleaseRunMembershipAsync(
                            run.WorkspaceId,
                            runId,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "failed to release workspace membership for run {RunId}", runIdValue);
                }
            }
        }
    }

    private async Task<bool> PersistExternalChangeAsync(
        WorkspaceId workspaceId,
        string workspaceRoot,
        WorkspaceExternalChangeEvent change,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Workspace external change detected: {Path} expected={ExpectedSha256} observed={ObservedSha256} writer={WriterKind}",
            change.NormalizedPath,
            change.ExpectedSha256,
            change.ObservedSha256,
            change.WriterKind);

        try
        {
            var changeId = $"chg_{Guid.NewGuid():N}";
            byte[]? expectedContent = null;
            _workspaceChangeMonitor?.TryGetExpectedContent(workspaceRoot, change.NormalizedPath, out expectedContent);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = change.Kind,
                expected_sha256 = change.ExpectedSha256,
                observed_sha256 = change.ObservedSha256,
                writer_kind = change.WriterKind,
                content_base64 = expectedContent is null ? null : Convert.ToBase64String(expectedContent),
            });
            var encrypted = await _workspaceJournalProtector.EncryptAsync(
                workspaceId,
                changeId,
                change.NormalizedPath,
                payload,
                cancellationToken).ConfigureAwait(false);
            await _workspaceCoordinatorStore.AppendChangeAsync(
                workspaceId,
                runId: null,
                changeId,
                change.NormalizedPath,
                change.ExpectedSha256,
                change.ObservedSha256,
                encrypted,
                "committed",
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError(error, "failed to persist external workspace change for {WorkspaceId}/{Path}", workspaceId.Value, change.NormalizedPath);
            return false;
        }
    }

    private static string NormalizeDriveFailureCode(Exception error)
    {
        if (error is InvalidDataException
            && error.Message.StartsWith("retained_input_", StringComparison.Ordinal))
        {
            return "retained_input_rehydration_failed";
        }

        if (error is InvalidDataException
            && error.Message.StartsWith("retained_approval_", StringComparison.Ordinal))
        {
            return error.Message == "retained_approval_expired"
                ? "approval_expired"
                : "retained_approval_rehydration_failed";
        }

        // Terminal codes must satisfy AgentRun/HarnessEventIntent validation:
        // 1..64 chars, starts [a-z], rest [a-z0-9_.]. Never embed raw messages.
        if (error is RuBackend.Harness.ModelRunner.ModelRunnerUnavailableException)
        {
            return "worker_unavailable";
        }

        if (error is RuBackend.Harness.ModelRunner.ModelRunnerProtocolException wpe)
        {
            var candidate = string.IsNullOrWhiteSpace(wpe.Code) ? wpe.Message : wpe.Code;
            if (IsStableTerminalCode(candidate))
            {
                return candidate;
            }

            var sanitized = SanitizeTerminalCodeSuffix(candidate);
            if (sanitized is not null)
            {
                var prefixed = "worker_" + sanitized;
                if (prefixed.Length <= 64)
                {
                    return prefixed;
                }
            }

            return "worker_failed";
        }

        if (error is RuBackend.Providers.ProviderCatalogException pce)
        {
            var candidate = pce.Message;
            if (IsStableTerminalCode(candidate))
            {
                return candidate.StartsWith("provider_", StringComparison.Ordinal)
                    ? candidate
                    : TryPrefix("provider_", candidate) ?? "provider_failed";
            }

            var sanitized = SanitizeTerminalCodeSuffix(candidate);
            if (sanitized is not null)
            {
                var prefixed = "provider_" + sanitized;
                if (prefixed.Length <= 64)
                {
                    return prefixed;
                }
            }

            return "provider_failed";
        }

        return "runtime_drive_failed";
    }

    private static string? TryPrefix(string prefix, string candidate)
    {
        if (candidate.Length + prefix.Length <= 64)
        {
            return prefix + candidate;
        }

        return null;
    }

    private static bool IsStableTerminalCode(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        foreach (var character in value)
        {
            var ok = (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character == '_'
                || character == '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string? SanitizeTerminalCodeSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(character);
            }
            else if (character is '_' or '.' or ' ' or '-' or ':' or '/' or '\\')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }
        }

        var sanitized = builder.ToString().Trim('_', '.');
        if (sanitized.Length == 0 || sanitized[0] is not (>= 'a' and <= 'z'))
        {
            return null;
        }

        if (sanitized.Length > 56)
        {
            sanitized = sanitized.Substring(0, 56).TrimEnd('_', '.');
        }

        return sanitized.Length == 0 ? null : sanitized;
    }

    private async Task PublishTerminalSummaryAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        if (_summaryPublisher is null && _lifecycleHooks is null)
        {
            return;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is { } terminal && terminal.State.IsTerminal())
        {
            await PublishRunFinishedAsync(terminal, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes a finished run's summary and starts its Stop hooks. Callers reach this
    /// before the run's registration is released, so its workspace root is still known.
    /// </summary>
    private async Task PublishRunFinishedAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_summaryPublisher is not null)
            await _summaryPublisher.TryPublishSummaryAsync(run, cancellationToken).ConfigureAwait(false);
        if (_lifecycleHooks is not null
            && run.State.IsTerminal()
            && _roots.TryGetRoot(run.RunId, out var workspaceRoot)
            && workspaceRoot is not null)
        {
            _lifecycleHooks.RunFinished(run, AgentV1Shared.MapRunState(run.Status), workspaceRoot);
        }
    }
}
