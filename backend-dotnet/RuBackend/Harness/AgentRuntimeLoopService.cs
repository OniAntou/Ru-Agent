using System.Collections.Concurrent;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;

namespace RuBackend.Harness;

/// <summary>
/// Registers the workspace root a run drives against. Roots are process-local: after a
/// host restart the loop skips driving until the run is reconciled (phase 3 Task 8).
/// </summary>
public interface IAgentRuntimeRootRegistry
{
    void Register(RunId runId, string workspaceRoot);

    bool TryGetRoot(RunId runId, out string? workspaceRoot);

    void Remove(RunId runId);

    IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot();
}

public sealed class AgentRuntimeRootRegistry : IAgentRuntimeRootRegistry
{
    private readonly ConcurrentDictionary<string, string> _roots = new(StringComparer.Ordinal);

    public void Register(RunId runId, string workspaceRoot) =>
        _roots[runId.Value] = workspaceRoot;

    public bool TryGetRoot(RunId runId, out string? workspaceRoot)
    {
        if (_roots.TryGetValue(runId.Value, out var root))
        {
            workspaceRoot = root;
            return true;
        }

        workspaceRoot = null;
        return false;
    }

    public void Remove(RunId runId) => _roots.TryRemove(runId.Value, out _);

    public IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot() =>
        _roots.Select(pair => (pair.Key, pair.Value)).ToArray();
}

/// <summary>
/// Creates one coordinator (and therefore one tool gateway authority) per run, rooted at
/// that run's trusted workspace instead of a process-wide directory.
/// </summary>
public interface IAgentRuntimeCoordinatorFactory
{
    AgentRunCoordinator Create(string workspaceRoot);
}

public sealed class AgentRuntimeCoordinatorFactory : IAgentRuntimeCoordinatorFactory
{
    /// <summary>
    /// Only tools the Host gateway actually implements are advertised on the headless
    /// path; anything else would fall through to the wrong execution branch.
    /// </summary>
    public static readonly IReadOnlyList<string> RuntimeCapabilities =
    [
        "files.list", "files.read", "files.glob", "files.search",
        "files.write", "files.edit", "files.delete", "files.restore",
        "terminal.run", "terminal.shell",
    ];

    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;

    public AgentRuntimeCoordinatorFactory(
        IModelStepRunner model,
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        HarnessPayloadStore payloadStore,
        ReconciliationInspectionGate? inspectionGate = null,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sqliteOptions = sqliteOptions ?? throw new ArgumentNullException(nameof(sqliteOptions));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _inspectionGate = inspectionGate;
        _approvalGrants = approvalGrants;
    }

    public AgentRunCoordinator Create(string workspaceRoot)
    {
        var gateway = new Broker.HarnessToolGateway(
            workspaceRoot,
            RuntimeCapabilities,
            presetProvider: _ => AgentPermissionPreset.Standard,
            idGenerator: new CryptographicIdGenerator(),
            approvalGrants: _approvalGrants);
        return new AgentRunCoordinator(
            _model,
            gateway,
            _store,
            new AgentContextAssembler(new SqliteModelContextSource(_sqliteOptions, _payloadStore)),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(64 * 1024),
            null,
            null,
            _inspectionGate);
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

    private readonly AgentConversationOutboxDispatcher _dispatcher;
    private readonly IHarnessEventStore _store;
    private readonly IAgentRuntimeCoordinatorFactory _coordinatorFactory;
    private readonly IAgentRuntimeRootRegistry _roots;
    private readonly ILogger<AgentRuntimeLoopService> _logger;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    public AgentRuntimeLoopService(
        AgentConversationOutboxDispatcher dispatcher,
        IHarnessEventStore store,
        IAgentRuntimeCoordinatorFactory coordinatorFactory,
        IAgentRuntimeRootRegistry roots,
        ILogger<AgentRuntimeLoopService> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinatorFactory = coordinatorFactory ?? throw new ArgumentNullException(nameof(coordinatorFactory));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        foreach (var (runId, workspaceRoot) in _roots.Snapshot())
        {
            await DriveRunIfRunningAsync(runId, workspaceRoot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DriveRunIfRunningAsync(
        string runIdValue,
        string workspaceRoot,
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
                    _roots.Remove(new RunId(runIdValue));
                }

                return;
            }

            await DriveRunAsync(runIdValue, workspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(runIdValue, out _);
        }
    }

    private async Task<AgentRun?> LoadRunAsync(string runIdValue, CancellationToken cancellationToken) =>
        await _store.GetRunAsync(new RunId(runIdValue), cancellationToken).ConfigureAwait(false);

    private async Task DriveRunAsync(string runIdValue, string workspaceRoot, CancellationToken cancellationToken)
    {
        var runId = new RunId(runIdValue);
        var coordinator = _coordinatorFactory.Create(workspaceRoot);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await coordinator.RunOneStepAsync(runId, cancellationToken).ConfigureAwait(false);
                if (result.Run.State == HarnessRunState.Running && result.ErrorCode is null)
                {
                    continue;
                }

                if (result.Run.State.IsTerminal())
                {
                    _roots.Remove(runId);
                }

                break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "run {RunId} failed during runtime drive", runIdValue);
            try
            {
                await coordinator.FailAsync(runId, "runtime_drive_failed", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception nested)
            {
                _logger.LogError(nested, "failed to mark run {RunId} as failed", runIdValue);
            }
        }
    }
}
