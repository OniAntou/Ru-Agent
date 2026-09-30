using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Harness;

namespace RuBackend.Harness.Reconciliation;

public sealed record RecoveredRunSummary(RunId RunId, string Action);

/// <summary>
/// Outcome of one recovery sweep. <see cref="ParkedCount"/> counts runs the sweep
/// could not handle at all (a store fault, a corrupt stream): they stay
/// non-terminal and keep their workspace locked, so the caller retries.
/// </summary>
public sealed record RecoverySweepResult(
    IReadOnlyList<RecoveredRunSummary> Recovered,
    int ParkedCount)
{
    public bool AllHandled => ParkedCount == 0;
}

/// <summary>
/// Startup reconciliation: for every non-terminal run this host may own, compare the
/// durable event stream against dispatch boundaries. A crash before any dispatch fails
/// the run safely with no effects; an incomplete read-only execution fails safely; an
/// incomplete side-effecting execution moves the run to ReconciliationRequired. No
/// startup path ever re-issues an old mutation execution id — ambiguous outcomes are
/// only ever resolved through the explicit reconcile endpoint.
/// </summary>
public sealed class HarnessRecoveryScanner(
    IHarnessEventStore store,
    ITimeProvider time,
    Func<CancellationToken, Task<IReadOnlyList<RunId>>>? pendingRunSource = null,
    ILogger? logger = null)
{
    private static readonly TimeSpan WorkspaceTrustTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when a run waits on an approval that has outlived the approval
    /// lifetime: every resolution attempt is refused as stale, so the run can
    /// never leave the wait through a client.
    /// </summary>
    public static bool IsExpiredApprovalWait(AgentRun run, DateTimeOffset now) =>
        run.State == HarnessRunState.WaitingApproval
        && ApprovalState.IsExpired(run.UpdatedAt, now);

    /// <summary>
    /// True when a run waits on a workspace-trust challenge that has outlived
    /// <see cref="WorkspaceTrustTimeout"/>: the decision endpoint refuses it as
    /// <c>trust_request_expired</c>, so the run can never leave the wait through a
    /// client and would hold its workspace's active-run lock forever.
    /// </summary>
    public static bool IsExpiredWorkspaceTrustWait(AgentRun run, DateTimeOffset now) =>
        run.State == HarnessRunState.WaitingWorkspaceTrust
        && run.UpdatedAt <= now - WorkspaceTrustTimeout;

    public async Task<RecoverySweepResult> SweepPendingRunsAsync(
        CancellationToken cancellationToken)
    {
        var summaries = new List<RecoveredRunSummary>();
        var parked = 0;
        var runIds = pendingRunSource is { } source
            ? await source(cancellationToken).ConfigureAwait(false)
            : Array.Empty<RunId>();
        foreach (var runId in runIds)
        {
            RecoveredRunSummary? summary;
            try
            {
                summary = await ScanRunAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // One unreadable run (corrupt events, store fault) must not abort
                // recovery of every other run: it stays non-terminal and the sweep
                // reports it so the caller can retry. It must be visible though: a
                // silently parked run keeps its workspace's active-run lock and
                // refuses new runs in that workspace.
                parked++;
                logger?.LogWarning(
                    error,
                    "run {RunId} could not be recovered and stays parked",
                    runId.Value);
                continue;
            }
            if (summary is { } recovered)
            {
                summaries.Add(recovered);
            }
        }

        return new RecoverySweepResult(summaries, parked);
    }

    public async Task<RecoveredRunSummary?> ScanRunAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.State.IsTerminal())
        {
            return null;
        }

        if (run.PermissionProfileVersion != AgentPermissionPresetMapper.CurrentPermissionProfileVersion)
        {
            return await FailRunAsync(run, "permission_profile_changed", cancellationToken)
                .ConfigureAwait(false);
        }

        // Workspace trust requests expire after ten minutes. If the host died or
        // the client abandoned the prompt, leaving this row non-terminal would
        // keep the workspace's unique active-run lock forever. Trust is a
        // pre-execution gate, so cancelling an expired request cannot roll back
        // any tool side effect.
        if (IsExpiredWorkspaceTrustWait(run, time.UtcNow))
        {
            return await CancelExpiredWorkspaceTrustAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (IsExpiredApprovalWait(run, time.UtcNow))
        {
            return await FailExpiredApprovalAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (run.State != HarnessRunState.Running)
        {
            return null;
        }

        var requested = new List<(
            ExecutionId ExecutionId,
            string? SafeCode,
            ModelStepId? ModelStepId,
            ToolCallId? ToolCallId,
            string? ProviderCallId)>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var hasToolProposal = false;
        await foreach (var envelope in store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var item = envelope.Event;
            if (item.Kind == "tool.execution_requested" && item.ExecutionId is { } executionId)
            {
                requested.Add((
                    executionId,
                    item.SafeCode,
                    item.ModelStepId,
                    item.ToolCallId,
                    item.ProviderCallId));
            }
            else if (item.Kind == "tool.execution_completed"
                     && item.ExecutionId is { } completedId)
            {
                _ = completed.Add(completedId.Value);
            }
            else if (item.Kind == "tool.proposed")
            {
                hasToolProposal = true;
            }
        }

        var incomplete = requested
            .Where(item => !completed.Contains(item.ExecutionId.Value))
            .ToList();

        if (requested.Count == 0)
        {
            if (hasToolProposal)
            {
                return null;
            }

            // The process died before any tool dispatch: no effects are possible.
            return await FailRunAsync(run, "recovery_pre_dispatch", cancellationToken)
                .ConfigureAwait(false);
        }

        if (incomplete.Count == 0)
        {
            // Every dispatched execution has a durable receipt: the run is simply mid-flight.
            return null;
        }

        if (incomplete.All(item => item.SafeCode == "read_only"))
        {
            return await FailRunAsync(run, "recovery_read_only_incomplete", cancellationToken).ConfigureAwait(false);
        }

        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.RequireReconciliation);
        if (!transition.Accepted)
        {
            return null;
        }

        var next = Advance(run, transition.Next, 1, time.UtcNow);
        var result = await store.CommitAsync(
                new HarnessCommit(
                    run.RunId,
                    run.Version,
                    next,
                    new[]
                    {
                        new HarnessEventIntent(
                            new RuAgent.Harness.Core.EventId($"evt_{Guid.NewGuid():N}"),
                            HarnessEventKind.ReconciliationRequired,
                            modelStepId: incomplete[0].ModelStepId,
                            toolCallId: incomplete[0].ToolCallId,
                            providerCallId: incomplete[0].ProviderCallId,
                            executionId: incomplete[0].ExecutionId,
                            safeCode: "recovery_ambiguous"),
                    },
                    Array.Empty<HarnessPayloadIntent>(),
                    Array.Empty<ConversationOutboxIntent>()),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == HarnessCommitOutcome.Committed
            ? new RecoveredRunSummary(run.RunId, "reconciliation_required")
            : null;
    }

    private static AgentRun Advance(
        AgentRun current,
        HarnessRunStatus nextStatus,
        long appendedCount,
        DateTimeOffset updatedAt,
        string? terminalErrorCode = null)
    {
        DateTimeOffset? terminalAt = nextStatus.State.IsTerminal() ? updatedAt : null;
        return new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            nextStatus,
            current.Version + 1,
            current.LastSequence + appendedCount,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            updatedAt,
            terminalAt,
            terminalErrorCode,
            current.PermissionProfileVersion);
    }

    private async Task<RecoveredRunSummary?> FailRunAsync(
        AgentRun run,
        string safeCode,
        CancellationToken cancellationToken)
    {
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Fail);
        if (!transition.Accepted)
        {
            return null;
        }

        var next = Advance(run, transition.Next, 1, time.UtcNow, safeCode);
        var result = await store.CommitAsync(
                new HarnessCommit(
                    run.RunId,
                    run.Version,
                    next,
                    new[]
                    {
                        new HarnessEventIntent(
                            new RuAgent.Harness.Core.EventId($"evt_{Guid.NewGuid():N}"),
                            HarnessEventKind.RunFailed,
                            safeCode: safeCode),
                    },
                    Array.Empty<HarnessPayloadIntent>(),
                    Array.Empty<ConversationOutboxIntent>()),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == HarnessCommitOutcome.Committed
            ? new RecoveredRunSummary(run.RunId, safeCode switch
            {
                "recovery_read_only_incomplete" => "failed_read_only_incomplete",
                "approval_expired" => "failed_approval_expired",
                "permission_profile_changed" => "failed_permission_profile_changed",
                _ => "failed_pre_dispatch",
            })
            : null;
    }

    /// <summary>
    /// Fails an abandoned approval wait so its workspace's active-run lock is
    /// released. The approval gate is pre-execution: the gated tool call was never
    /// dispatched, so failing the run cannot roll back a tool side effect.
    /// </summary>
    public async Task<RecoveredRunSummary?> FailExpiredApprovalAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (!IsExpiredApprovalWait(run, time.UtcNow))
        {
            return null;
        }

        return await FailRunAsync(run, "approval_expired", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels an abandoned workspace-trust wait so its workspace's active-run lock is
    /// released. Trust is a pre-execution gate, so cancelling an expired challenge cannot
    /// roll back a tool side effect. Called both by the startup sweep and by the runtime
    /// loop, so a long-lived host does not park the run until the next restart.
    /// </summary>
    public async Task<RecoveredRunSummary?> CancelExpiredWorkspaceTrustAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Cancel);
        if (!transition.Accepted)
        {
            return null;
        }

        const string safeCode = "workspace_trust_expired";
        var next = Advance(run, transition.Next, 1, time.UtcNow, safeCode);
        var result = await store.CommitAsync(
                new HarnessCommit(
                    run.RunId,
                    run.Version,
                    next,
                    new[]
                    {
                        new HarnessEventIntent(
                            new RuAgent.Harness.Core.EventId($"evt_{Guid.NewGuid():N}"),
                            HarnessEventKind.RunStateChanged,
                            safeCode: safeCode),
                    },
                    Array.Empty<HarnessPayloadIntent>(),
                    Array.Empty<ConversationOutboxIntent>()),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == HarnessCommitOutcome.Committed
            ? new RecoveredRunSummary(run.RunId, "cancelled_workspace_trust_expired")
            : null;
    }
}

/// <summary>
/// Runs the recovery scan once at host startup over every non-terminal run in the
/// harness database. Runs whose workspace root this process does not hold stay parked
/// until the runtime loop or an explicit reconcile resolves them.
/// </summary>
public sealed class HarnessRecoveryStartupService : BackgroundService
{
    /// <summary>
    /// A transient startup fault (a database lock while the Broker opens the same
    /// file, one unreadable run) must not leave every non-terminal run parked:
    /// parked runs keep their workspace's active-run lock, so new runs in those
    /// workspaces are refused until the next runtime start.
    /// </summary>
    private const int MaximumAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly ILogger<HarnessRecoveryStartupService> _logger;
    private readonly WorkspaceTrustService? _trustService;
    private readonly SqliteWorkspaceStore? _workspaceStore;
    private readonly HarnessPolicySnapshotService? _policySnapshots;
    private readonly IAgentRuntimeRootRegistry? _runtimeRoots;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly ReconciliationInspectionGate? _inspectionGate;

    public HarnessRecoveryStartupService(
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        ILogger<HarnessRecoveryStartupService> logger,
        WorkspaceTrustService? trustService = null,
        SqliteWorkspaceStore? workspaceStore = null,
        HarnessPolicySnapshotService? policySnapshots = null,
        IAgentRuntimeRootRegistry? runtimeRoots = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        ReconciliationInspectionGate? inspectionGate = null)
    {
        _store = store;
        _sqliteOptions = sqliteOptions;
        _logger = logger;
        _trustService = trustService;
        _workspaceStore = workspaceStore;
        _policySnapshots = policySnapshots;
        _runtimeRoots = runtimeRoots;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _inspectionGate = inspectionGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            if (await TrySweepAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            if (attempt == MaximumAttempts)
            {
                // A parked run keeps its workspace's active-run lock, so every new
                // run in that workspace is refused until a later runtime start
                // retries. Give up loudly instead of parking silently.
                _logger.LogError(
                    "startup recovery gave up after {Attempts} attempts; parked runs keep their workspaces locked until the next runtime start",
                    attempt);
                return;
            }

            _logger.LogWarning(
                "startup recovery attempt {Attempt} failed; retrying in {DelaySeconds}s",
                attempt,
                RetryDelay.TotalSeconds);
            try
            {
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> TrySweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Recovery queries agent_runs directly, so make the schema an explicit
            // dependency instead of racing the warmup hosted service on a fresh
            // database where the table does not exist yet.
            var health = await HarnessSqliteSchema
                .InitializeAsync(_sqliteOptions, stoppingToken)
                .ConfigureAwait(false);
            if (!health.IsAvailable)
            {
                _logger.LogWarning(
                    "startup recovery postponed: harness store unavailable ({ErrorCode})",
                    health.ErrorCode);
                return false;
            }

            var pendingRunIds = await ListNonTerminalRunIdsAsync(stoppingToken).ConfigureAwait(false);
            var scanned = await ScanRunsStageAsync(pendingRunIds, stoppingToken).ConfigureAwait(false);
            await RearmInspectionGatesAsync(pendingRunIds, stoppingToken).ConfigureAwait(false);
            var reconciled = await ReconcileMembershipsStageAsync(stoppingToken).ConfigureAwait(false);
            await RehydrateRuntimeRootsAsync(pendingRunIds, stoppingToken).ConfigureAwait(false);
            return scanned && reconciled;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "startup recovery scan failed");
            return false;
        }
    }

    private async Task<bool> ScanRunsStageAsync(IReadOnlyList<RunId> pendingRunIds, CancellationToken stoppingToken)
    {
        try
        {
            var scanner = new HarnessRecoveryScanner(
                _store,
                new SystemTimeProvider(),
                _ => Task.FromResult<IReadOnlyList<RunId>>(pendingRunIds),
                _logger);
            var sweep = await scanner.SweepPendingRunsAsync(stoppingToken).ConfigureAwait(false);
            foreach (var summary in sweep.Recovered)
            {
                _logger.LogWarning(
                    "startup recovery marked run {RunId} as {Action}",
                    summary.RunId.Value,
                    summary.Action);
            }

            return sweep.AllHandled;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The run scan must not skip workspace membership reconciliation:
            // stale leases alone can keep every workspace locked. The failed
            // attempt is reported so the sweep can retry it.
            _logger.LogError(error, "startup recovery run scan failed");
            return false;
        }
    }

    /// <summary>
    /// Re-arms the mutation gate for every non-terminal run whose durable history
    /// still says its mutations are gated. The gate is process-local, so without
    /// this a host restart silently released a run the user had put under
    /// inspection and let mutations through again.
    /// </summary>
    private async Task RearmInspectionGatesAsync(
        IReadOnlyList<RunId> pendingRunIds,
        CancellationToken stoppingToken)
    {
        if (_inspectionGate is null || pendingRunIds.Count == 0)
        {
            return;
        }

        var armed = new List<RunId>();
        foreach (var runId in pendingRunIds)
        {
            try
            {
                var events = new List<RuAgent.Harness.Core.HarnessEvent>();
                await foreach (var envelope in _store.ReadEventsAsync(runId, 0, stoppingToken).ConfigureAwait(false))
                {
                    events.Add(envelope.Event);
                }

                if (RuAgent.Harness.Core.Reconciliation.ReconciliationGateProjection
                    .IsMutationDenied(events))
                {
                    armed.Add(runId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // An unreadable run must not abort re-arming the others. Fail closed:
                // if the history cannot be read, keep the gate on rather than let a
                // possibly-gated run mutate.
                _logger.LogWarning(
                    error,
                    "could not rebuild the inspection gate for run {RunId}; keeping it armed",
                    runId.Value);
                armed.Add(runId);
            }
        }

        if (armed.Count == 0)
        {
            return;
        }

        _inspectionGate.Seed(armed);
        _logger.LogWarning(
            "re-armed the mutation inspection gate for {Count} run(s) after restart",
            armed.Count);
    }

    private async Task<bool> ReconcileMembershipsStageAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_workspaceCoordinatorStore is null)
            {
                return true;
            }
            var reconciled = await _workspaceCoordinatorStore
                .ReconcileExpiredAsync(DateTimeOffset.UtcNow, stoppingToken)
                .ConfigureAwait(false);
            if (reconciled.ExpiredMemberships > 0
                || reconciled.ExpiredClaims > 0
                || reconciled.RejectedPreparedChanges > 0)
            {
                _logger.LogWarning(
                    "workspace recovery reconciled memberships={Memberships} claims={Claims} prepared_changes={PreparedChanges}",
                    reconciled.ExpiredMemberships,
                    reconciled.ExpiredClaims,
                    reconciled.RejectedPreparedChanges);
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "startup recovery membership reconcile failed");
            return false;
        }
    }

    private async Task RehydrateRuntimeRootsAsync(
        IReadOnlyList<RunId> runIds,
        CancellationToken cancellationToken)
    {
        if (_trustService is null
            || _workspaceStore is null
            || _policySnapshots is null
            || _runtimeRoots is null)
        {
            return;
        }

        foreach (var runId in runIds)
        {
            try
            {
                var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
                if (run is null || run.State.IsTerminal())
                {
                    continue;
                }

                // Parked runs (WaitingUser, ReconciliationRequired) are registered too:
                // Continue moves them to Running, and the drive loop can only pick up a
                // run whose root this process holds (AgentRuntimeLoopService.StepOnceAsync
                // drives _roots.Snapshot()). Registering a parked run does not start it:
                // DriveRunIfRunningAsync skips every state but Running/WaitingApproval.

                var storedWorkspace = await _workspaceStore
                    .GetAsync(run.WorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
                if (storedWorkspace is null)
                {
                    _logger.LogWarning("run {RunId} has no persisted workspace registration; keeping it parked", runId.Value);
                    continue;
                }

                var root = await _trustService
                    .DecryptCanonicalRootAsync(storedWorkspace, cancellationToken)
                    .ConfigureAwait(false);
                var snapshot = await _policySnapshots
                    .LoadAsync(storedWorkspace.Identity, run.Preset, run.RunId, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.Version != run.PolicySnapshotVersion
                    || !string.Equals(snapshot.Sha256, run.PolicySnapshotSha256, StringComparison.Ordinal))
                {
                    _logger.LogError(
                        "run {RunId} policy snapshot no longer matches its durable contract; keeping it parked",
                        runId.Value);
                    continue;
                }

                _runtimeRoots.Register(run.RunId, root, snapshot);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Keeping the run parked is the right outcome for an unreadable
                // registration, but an unexpected fault must not abort the sweep:
                // the remaining runs still need their workspace locks back.
                _logger.LogWarning(error, "run {RunId} could not be rehydrated; keeping it parked", runId.Value);
            }
        }
    }

    private async Task<IReadOnlyList<RunId>> ListNonTerminalRunIdsAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _sqliteOptions.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,

            // No pooling: a pooled handle would keep the database file locked and break
            // temporary-data cleanup in tests and shutdown paths.
            Pooling = false,
        }.ToString();
        var ids = new List<RunId>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM agent_runs WHERE terminal_at_utc IS NULL";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(new RunId(reader.GetString(0)));
        }

        return ids;
    }
}
