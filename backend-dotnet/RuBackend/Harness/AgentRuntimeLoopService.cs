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
using RuAgent.Broker.Tools.Help;
using RuBackend.Business.Settings;
using RuBackend.Harness.Prompting;

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
    AgentRunCoordinator Create(string workspaceRoot, PolicySnapshot? policySnapshot = null);
}

public sealed class AgentRuntimeCoordinatorFactory : IAgentRuntimeCoordinatorFactory
{
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;
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
    private readonly HelpDocsTool _helpDocs;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;

    public AgentRuntimeCoordinatorFactory(
        IModelStepRunner model,
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        HarnessPayloadStore payloadStore,
        ReconciliationInspectionGate? inspectionGate = null,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null,
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
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sqliteOptions = sqliteOptions ?? throw new ArgumentNullException(nameof(sqliteOptions));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _inspectionGate = inspectionGate;
        _approvalGrants = approvalGrants;
        _transientRedactor = transientRedactor;
        _liveEvents = liveEvents;
        _summaryPublisher = summaryPublisher;
        _replyPolicy = replyPolicy;
        _replyEffects = replyEffects;
        _instructionLoader = instructionLoader ?? new RuInstructionLoader();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _contextLimit = contextLimit
            ?? new StaticModelContextLimitProvider(32_768);
        _webExecutor = webExecutor;
        _helpDocs = HelpDocsTool.CreateDefault();
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
    }

    public AgentRunCoordinator Create(string workspaceRoot, PolicySnapshot? policySnapshot = null)
    {
        var availability = new AgentToolRuntimeAvailability(
            _webExecutor?.IsSearchEnabled == true,
            _webExecutor?.IsFetchEnabled == true);
        var projectedDefinitions = AgentToolRuntimeProjection.Select(availability);
        var gateway = new Broker.HarnessToolGateway(
            workspaceRoot,
            projectedDefinitions.Select(definition => definition.ToolId).ToArray(),
            presetProvider: runId => policySnapshot?.Preset ?? AgentPermissionPreset.Standard,
            idGenerator: new CryptographicIdGenerator(),
            approvalGrants: _approvalGrants,
            policySnapshot: policySnapshot,
            webExecutor: _webExecutor,
            helpDocsTool: _helpDocs,
            workspaceCoordinatorStore: _workspaceCoordinatorStore,
            eventStore: _store);
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
            promptContextProvider);
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

    public AgentRuntimeLoopService(
        AgentConversationOutboxDispatcher dispatcher,
        IHarnessEventStore store,
        IAgentRuntimeCoordinatorFactory coordinatorFactory,
        IAgentRuntimeRootRegistry roots,
        ILogger<AgentRuntimeLoopService> logger,
        AgentRunSummaryLivePublisher? summaryPublisher = null,
        WorkspaceExternalChangeMonitor? workspaceChangeMonitor = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        WorkspaceJournalProtector? workspaceJournalProtector = null)
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "agent runtime loop iteration failed");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task StepOnceAsync(CancellationToken cancellationToken)
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
            return;
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
                if (run is { } loaded && loaded.State.IsTerminal())
                {
                    _summaryPublisher?.TryPublishSummary(loaded);
                    _roots.Remove(new RunId(runIdValue));
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

    private async Task<AgentRun?> LoadRunAsync(string runIdValue, CancellationToken cancellationToken) =>
        await _store.GetRunAsync(new RunId(runIdValue), cancellationToken).ConfigureAwait(false);

    private async Task DriveRunAsync(string runIdValue, string workspaceRoot, PolicySnapshot? policySnapshot, CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        AgentRunCoordinator? coordinator = null;
        IDisposable? workspaceWatcher = null;
        WorkspaceRunMembershipRecord? membership = null;
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
                try
                {
                    membership = await _workspaceCoordinatorStore.AcquireRunMembershipAsync(
                        workspaceRun.WorkspaceId,
                        runId,
                        "ru_peer",
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromSeconds(workspace.LeaseTtlSeconds),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessStoreException exception) when (exception.Code == "workspace_busy")
                {
                    _logger.LogInformation("run {RunId} is waiting for a workspace membership lease", runIdValue);
                    return;
                }
            }

            if (_workspaceChangeMonitor is not null)
            {
                _workspaceChangeMonitor.CaptureExpectedHashes(workspaceRoot);
                workspaceWatcher = _workspaceChangeMonitor.StartWatchingAsync(
                    workspaceRoot,
                    change => PersistExternalChangeAsync(workspaceRun.WorkspaceId, workspaceRoot, change, CancellationToken.None));
            }
            coordinator = _coordinatorFactory.Create(workspaceRoot, policySnapshot);
            _roots.RegisterCoordinator(runId, coordinator);

            if (policySnapshot is null)
            {
                await coordinator.FailAsync(runId, "policy_snapshot_unavailable", cancellationToken).ConfigureAwait(false);
                await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (membership is not null && _workspaceCoordinatorStore is not null)
                {
                    var renewed = await _workspaceCoordinatorStore.RenewRunMembershipAsync(
                        workspaceRun.WorkspaceId,
                        runId,
                        DateTimeOffset.UtcNow,
                        membership.LeaseExpiresAtUtc - membership.HeartbeatAtUtc,
                        cancellationToken).ConfigureAwait(false);
                    if (!renewed)
                    {
                        await coordinator.FailAsync(runId, "workspace_membership_expired", cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }

                var result = await coordinator.RunOneStepAsync(runId, cancellationToken).ConfigureAwait(false);
                if (result.Run.State == HarnessRunState.Running && result.ErrorCode is null)
                {
                    continue;
                }

                if (result.Run.State.IsTerminal())
                {
                    _summaryPublisher?.TryPublishSummary(result.Run);
                    _roots.Remove(runId);
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
                _roots.Remove(runId);
                return;
            }

            try
            {
                await coordinator.FailAsync(runId, "runtime_drive_failed", cancellationToken).ConfigureAwait(false);
                await PublishTerminalSummaryAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception nested)
            {
                _logger.LogError(nested, "failed to mark run {RunId} as failed", runIdValue);
            }
        }
        finally
        {
            workspaceWatcher?.Dispose();
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
