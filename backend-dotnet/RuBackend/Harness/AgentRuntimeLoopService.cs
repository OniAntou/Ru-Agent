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
using RuBackend.Harness.Prompting;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness;

/// <summary>
/// Registers the workspace root a run drives against. Roots are process-local: after a
/// host restart the loop skips driving until the run is reconciled (phase 3 Task 8).
/// </summary>
public interface IAgentRuntimeRootRegistry
{
    void Register(RunId runId, string workspaceRoot);

    void Register(RunId runId, string workspaceRoot, PolicySnapshot policySnapshot);

    bool TryGetRoot(RunId runId, out string? workspaceRoot);

    bool TryGetPolicySnapshot(RunId runId, out PolicySnapshot? policySnapshot);

    void RegisterCoordinator(RunId runId, AgentRunCoordinator coordinator);

    bool TryGetCoordinator(RunId runId, out AgentRunCoordinator? coordinator);

    Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken);

    void Remove(RunId runId);

    IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot();
}

public sealed class AgentRuntimeRootRegistry : IAgentRuntimeRootRegistry
{
    private sealed record Registration(
        string WorkspaceRoot,
        PolicySnapshot? PolicySnapshot,
        AgentRunCoordinator? Coordinator = null);

    private readonly ConcurrentDictionary<string, Registration> _roots = new(StringComparer.Ordinal);

    public void Register(RunId runId, string workspaceRoot) =>
        _roots.AddOrUpdate(
            runId.Value,
            _ => new Registration(workspaceRoot, null),
            (_, existing) => existing with { WorkspaceRoot = workspaceRoot });

    public void Register(RunId runId, string workspaceRoot, PolicySnapshot policySnapshot)
    {
        ArgumentNullException.ThrowIfNull(policySnapshot);
        _roots.AddOrUpdate(
            runId.Value,
            _ => new Registration(workspaceRoot, policySnapshot),
            (_, existing) => existing with
            {
                WorkspaceRoot = workspaceRoot,
                PolicySnapshot = policySnapshot,
            });
    }

    public bool TryGetRoot(RunId runId, out string? workspaceRoot)
    {
        if (_roots.TryGetValue(runId.Value, out var registration))
        {
            workspaceRoot = registration.WorkspaceRoot;
            return true;
        }

        workspaceRoot = null;
        return false;
    }

    public bool TryGetPolicySnapshot(RunId runId, out PolicySnapshot? policySnapshot)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.PolicySnapshot is { } snapshot)
        {
            policySnapshot = snapshot;
            return true;
        }

        policySnapshot = null;
        return false;
    }

    public void RegisterCoordinator(RunId runId, AgentRunCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        while (_roots.TryGetValue(runId.Value, out var existing))
        {
            if (_roots.TryUpdate(
                    runId.Value,
                    existing with { Coordinator = coordinator },
                    existing))
            {
                return;
            }
        }
    }

    public bool TryGetCoordinator(RunId runId, out AgentRunCoordinator? coordinator)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.Coordinator is { } value)
        {
            coordinator = value;
            return true;
        }

        coordinator = null;
        return false;
    }

    public async Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.Coordinator is { } coordinator)
        {
            await coordinator.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Remove(RunId runId) => _roots.TryRemove(runId.Value, out _);

    public IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot() =>
        _roots.Select(pair => (pair.Key, pair.Value.WorkspaceRoot)).ToArray();
}

/// <summary>
/// Creates one coordinator (and therefore one tool gateway authority) per run, rooted at
/// that run's trusted workspace instead of a process-wide directory.
/// </summary>
public interface IAgentRuntimeCoordinatorFactory
{
    AgentRunCoordinator Create(
        string workspaceRoot,
        PolicySnapshot? policySnapshot = null,
        RunId? runId = null,
        string? agentSessionId = null);

    void DropRetainedApproval(RunId runId);
}

public sealed class AgentRuntimeCoordinatorFactory : IAgentRuntimeCoordinatorFactory
{
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly ITransientContentRedactor? _transientRedactor;
    private readonly HarnessLiveEventHub? _liveEvents;
    private readonly AgentRunSummaryLivePublisher? _summaryPublisher;
    private readonly IAgentReplyPolicy? _replyPolicy;
    private readonly IAgentReplyEffectSink? _replyEffects;
    private readonly RuInstructionLoader _instructionLoader;
    private readonly ISettingsStore _settings;
    private readonly ITimeProvider _time;
    private readonly IModelContextLimitProvider _contextLimit;
    private readonly Broker.AgentWebToolExecutor? _webExecutor;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly InMemoryRetainedApprovalStash _retainedStash;

    public AgentRuntimeCoordinatorFactory(
        IModelStepRunner model,
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        HarnessPayloadStore payloadStore,
        ReconciliationInspectionGate? inspectionGate = null,
        ITransientContentRedactor? transientRedactor = null,
        HarnessLiveEventHub? liveEvents = null,
        AgentRunSummaryLivePublisher? summaryPublisher = null,
        IAgentReplyPolicy? replyPolicy = null,
        IAgentReplyEffectSink? replyEffects = null,
        RuInstructionLoader? instructionLoader = null,
        ISettingsStore? settings = null,
        ITimeProvider? time = null,
        IModelContextLimitProvider? contextLimit = null,
        Broker.AgentWebToolExecutor? webExecutor = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        Broker.BrokerToolGatewayFactory? brokerGateways = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sqliteOptions = sqliteOptions ?? throw new ArgumentNullException(nameof(sqliteOptions));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _inspectionGate = inspectionGate;
        _transientRedactor = transientRedactor;
        _liveEvents = liveEvents;
        _summaryPublisher = summaryPublisher;
        _replyPolicy = replyPolicy;
        _replyEffects = replyEffects;
        _instructionLoader = instructionLoader ?? new RuInstructionLoader();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _contextLimit = contextLimit
            ?? new DynamicModelContextLimitProvider(_settings);
        _webExecutor = webExecutor;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _brokerGateways = brokerGateways;
        _retainedStash = new InMemoryRetainedApprovalStash(_time);
    }

    public void DropRetainedApproval(RunId runId) => _retainedStash.Drop(runId);

    public AgentRunCoordinator Create(
        string workspaceRoot,
        PolicySnapshot? policySnapshot = null,
        RunId? runId = null,
        string? agentSessionId = null)
    {
        var availability = new AgentToolRuntimeAvailability(
            _webExecutor?.IsSearchEnabled == true,
            _webExecutor?.IsFetchEnabled == true);
        if (_brokerGateways is not { IsConfigured: true }
            || policySnapshot is null
            || runId is not { } brokerRunId)
        {
            throw new InvalidOperationException(
                "broker_gateway_unavailable: every tool call must execute inside the Broker");
        }

        IToolGateway gateway = _brokerGateways.Create(workspaceRoot, policySnapshot, brokerRunId, agentSessionId);
        var frames = new TransientFrameBuffer(
            64 * 1024,
            _liveEvents is null ? null : frame => PublishLiveFrame(_liveEvents, frame));
        IAgentActivitySink? activitySink = _summaryPublisher is not null
            ? _summaryPublisher
            : _liveEvents is null
                ? null
                : new AgentActivityLivePublisher(_liveEvents);
        var promptContextProvider = new RuPromptContextProvider(
            workspaceRoot,
            _instructionLoader,
            _settings,
            _time,
            availability,
            _workspaceCoordinatorStore);
        return new AgentRunCoordinator(
            _model,
            gateway,
            _store,
            new AgentContextAssembler(new SqliteModelContextSource(_sqliteOptions, _payloadStore)),
            new CryptographicIdGenerator(),
            _time,
            new ToolCallScheduler(),
            frames,
            _transientRedactor ?? KnownSecretRedactor.Empty,
            _contextLimit,
            _inspectionGate,
            activitySink,
            _replyPolicy,
            _replyEffects,
            promptContextProvider,
            retainedStash: _retainedStash);
    }

    private static void PublishLiveFrame(HarnessLiveEventHub hub, HarnessTransientFrame frame)
    {
        var kind = frame.Kind == TransientFrameKind.AssistantDelta
            ? "assistant.delta"
            : "tool.output.delta";
        var data = JsonSerializer.Serialize(new
        {
            text = frame.Content,
            model_step_id = frame.ModelStepId?.Value,
            execution_id = frame.ExecutionId?.Value,
            // Emitted only for a superseded attempt: the desktop client's delta
            // parser rejects unknown fields, so an ordinary frame must keep the
            // exact shape it has always had.
            reset = frame.IsReset ? true : (bool?)null,
        });
        hub.TryPublish(frame.RunId, kind, data);
    }
}

/// <summary>
/// Drives the headless coding loop: delivers conversation-outbox transitions through the
/// dispatcher, then runs model/tool steps for every running run it holds a workspace
/// root for. Runs stop being driven when they block on approval/history or complete.
/// </summary>
public sealed class AgentRuntimeLoopService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The interval used when no run is registered. The loop is the only thing that
    /// can start a registered run, so an idle host must still poll — but at a quarter
    /// of the active rate. This is a desktop agent that stays open for hours: four
    /// wakeups per second with a database round trip each, while the user is doing
    /// something else, is pure idle cost. The cost of the backoff is a sub-second
    /// delay before a newly created run is picked up, which the user cannot perceive
    /// because the first model step takes far longer than that.
    /// </summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(1000);

    private const int MaxConcurrentRuns = 4;

    private readonly AgentConversationOutboxDispatcher _dispatcher;
    private readonly IHarnessEventStore _store;
    private readonly IAgentRuntimeCoordinatorFactory _coordinatorFactory;
    private readonly IAgentRuntimeRootRegistry _roots;
    private readonly ILogger<AgentRuntimeLoopService> _logger;
    private readonly AgentRunSummaryLivePublisher? _summaryPublisher;
    private readonly WorkspaceExternalChangeMonitor? _workspaceChangeMonitor;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector? _workspaceJournalProtector;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly HarnessRecoveryScanner _recoveryScanner;

    public AgentRuntimeLoopService(
        AgentConversationOutboxDispatcher dispatcher,
        IHarnessEventStore store,
        IAgentRuntimeCoordinatorFactory coordinatorFactory,
        IAgentRuntimeRootRegistry roots,
        ILogger<AgentRuntimeLoopService> logger,
        AgentRunSummaryLivePublisher? summaryPublisher = null,
        WorkspaceExternalChangeMonitor? workspaceChangeMonitor = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        WorkspaceJournalProtector? workspaceJournalProtector = null,
        Broker.BrokerToolGatewayFactory? brokerGateways = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinatorFactory = coordinatorFactory ?? throw new ArgumentNullException(nameof(coordinatorFactory));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _summaryPublisher = summaryPublisher;
        _workspaceChangeMonitor = workspaceChangeMonitor;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _workspaceJournalProtector = workspaceJournalProtector;
        _brokerGateways = brokerGateways;
        _recoveryScanner = new HarnessRecoveryScanner(_store, new SystemTimeProvider());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = IdlePollInterval;
            try
            {
                var hasActiveRuns = await StepOnceAsync(stoppingToken).ConfigureAwait(false);
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

            await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one iteration. Returns true when at least one run is registered, which is
    /// the signal the loop uses to choose its next poll interval.
    /// </summary>
    private async Task<bool> StepOnceAsync(CancellationToken cancellationToken)
    {
        // Outbox delivery first so history transitions land before any new model step.
        var dispatch = await _dispatcher.DispatchOnceAsync(cancellationToken).ConfigureAwait(false);

        // Drive every registered running run on every iteration: state can become
        // runnable without an outbox write too (e.g. approval_resolved after a V1
        // approve), so delivery alone is not a sufficient trigger.
        _ = dispatch;
        var registrations = _roots.Snapshot();
        if (registrations.Count == 0)
        {
            return false;
        }

        // A long-running terminal command must not stall every other run. Keep a
        // bounded degree of concurrency so a burst of runs cannot exhaust the host
        // with model/process work while still allowing independent runs to progress.
        using var gate = new SemaphoreSlim(MaxConcurrentRuns, MaxConcurrentRuns);
        var tasks = registrations.Select(async registration =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runId = new RunId(registration.RunId);
                _roots.TryGetPolicySnapshot(runId, out var policySnapshot);
                await DriveRunIfRunningAsync(
                        registration.RunId,
                        registration.WorkspaceRoot,
                        policySnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return true;
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
            if (run is null || run.State != HarnessRunState.Running)
            {
                if (run is { } loaded)
                {
                    if (loaded.State.IsTerminal())
                    {
                        _summaryPublisher?.TryPublishSummary(loaded);
                        _roots.Remove(new RunId(runIdValue));
                    }
                    else if (HarnessRecoveryScanner.IsExpiredApprovalWait(loaded, DateTimeOffset.UtcNow))
                    {
                        // The durable approval is past its lifetime, so no client can
                        // resolve it any more. Fail the run now instead of leaving it
                        // parked with the workspace's unique active-run lock.
                        await ReleaseExpiredApprovalAsync(runIdValue, loaded, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                return;
            }

            await DriveRunAsync(runIdValue, workspaceRoot, policySnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(runIdValue, out _);
        }
    }

    private void RemoveRunRegistration(RunId runId)
    {
        _roots.Remove(runId);
        _brokerGateways?.CloseRun(runId.Value);
        _coordinatorFactory.DropRetainedApproval(runId);
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
        RemoveRunRegistration(runId);
    }

    private async Task<AgentRun?> LoadRunAsync(string runIdValue, CancellationToken cancellationToken) =>
        await _store.GetRunAsync(new RunId(runIdValue), cancellationToken).ConfigureAwait(false);

    private async Task DriveRunAsync(string runIdValue, string workspaceRoot, PolicySnapshot? policySnapshot, CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        AgentRunCoordinator? coordinator = null;
        IDisposable? workspaceWatcher = null;
        WorkspaceRunMembershipRecord? membership = null;
        WorkspaceMembershipHeartbeat? membershipHeartbeat = null;
        try
        {
            _summaryPublisher?.RegisterWorkspace(runId, workspaceRoot);
            var workspaceRun = await LoadRunAsync(runIdValue, cancellationToken).ConfigureAwait(false);
            if (workspaceRun is null)
            {
                return;
            }

            if (_workspaceCoordinatorStore is not null)
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

            if (_workspaceChangeMonitor is not null)
            {
                _workspaceChangeMonitor.CaptureExpectedHashes(workspaceRoot);
                workspaceWatcher = _workspaceChangeMonitor.StartWatchingAsync(
                    workspaceRoot,
                    change => PersistExternalChangeAsync(workspaceRun.WorkspaceId, workspaceRoot, change, CancellationToken.None));
            }
            coordinator = _coordinatorFactory.Create(workspaceRoot, policySnapshot, runId, workspaceRun.SessionId.Value);
            _roots.RegisterCoordinator(runId, coordinator);

            if (policySnapshot is null)
            {
                await coordinator.FailAsync(runId, "policy_snapshot_unavailable", cancellationToken).ConfigureAwait(false);
                await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A previous drive may have parked proposals in an approval wait.
            // Resume them before the model steps: the receipts land in durable
            // state, so the next model step continues naturally. Null or stale
            // means nothing parked; stepping proceeds unchanged.
            await coordinator.TryResumeRetainedApprovalAsync(runId, cancellationToken).ConfigureAwait(false);

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
                        RemoveRunRegistration(runId);
                        break;
                    }
                }

                var result = await stepTask.ConfigureAwait(false);
                if (result.Run.State == HarnessRunState.Running && result.ErrorCode is null)
                {
                    continue;
                }

                // Triage hook: surface the terminal reason alongside the exception dump.
                var terminalDiagnostic = Environment.GetEnvironmentVariable("RU_DRIVE_FAILURE_DIAGNOSTIC");
                if (!string.IsNullOrWhiteSpace(terminalDiagnostic) && result.ErrorCode is not null)
                {
                    try
                    {
                        File.AppendAllText(
                            terminalDiagnostic,
                            $"{DateTimeOffset.UtcNow:O} {runIdValue}: error_code={result.ErrorCode} state={result.Run.State}{Environment.NewLine}");
                    }
                    catch (Exception)
                    {
                    }
                }

                if (result.Run.State.IsTerminal())
                {
                    _summaryPublisher?.TryPublishSummary(result.Run);
                    RemoveRunRegistration(runId);
                }

                break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Triage hook: the normalized terminal code hides the underlying exception,
            // which makes a drive failure hard to diagnose from a test. Set
            // RU_DRIVE_FAILURE_DIAGNOSTIC to a file path to dump the full exception.
            var diagnosticPath = Environment.GetEnvironmentVariable("RU_DRIVE_FAILURE_DIAGNOSTIC");
            if (!string.IsNullOrWhiteSpace(diagnosticPath))
            {
                try
                {
                    File.AppendAllText(diagnosticPath, $"{DateTimeOffset.UtcNow:O} {runIdValue}: {error}{Environment.NewLine}");
                }
                catch (Exception)
                {
                    // Diagnostics must never change run behaviour.
                }
            }

            _logger.LogWarning(error, "run {RunId} failed during runtime drive", runIdValue);
            if (coordinator is null)
            {
                // A missing/replaced workspace can make gateway construction fail
                // before a coordinator exists. Park the registration instead of
                // retrying the same bad root on every 250 ms poll.
                RemoveRunRegistration(runId);
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
            _workspaceChangeMonitor?.ClearExpectedHashes(workspaceRoot);
            if (membership is not null && _workspaceCoordinatorStore is not null)
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
                        await _workspaceCoordinatorStore.ReleasePathClaimsAsync(
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

    private async Task PersistExternalChangeAsync(
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

        if (_workspaceCoordinatorStore is null || _workspaceJournalProtector is null)
        {
            return;
        }

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
            _workspaceChangeMonitor?.RegisterExpectedHash(workspaceRoot, change.NormalizedPath, change.ObservedSha256);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError(error, "failed to persist external workspace change for {WorkspaceId}/{Path}", workspaceId.Value, change.NormalizedPath);
        }
    }

    private static string NormalizeDriveFailureCode(Exception error)
    {
        // Terminal codes must satisfy AgentRun/HarnessEventIntent validation:
        // 1..64 chars, starts [a-z], rest [a-z0-9_.]. Never embed raw messages.
        if (error is RuBackend.Worker.WorkerUnavailableException)
        {
            return "worker_unavailable";
        }

        if (error is RuBackend.Worker.WorkerProtocolException wpe)
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
        if (_summaryPublisher is null)
        {
            return;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is { } terminal && terminal.State.IsTerminal())
        {
            _summaryPublisher.TryPublishSummary(terminal);
        }
    }
}
