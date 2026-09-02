using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Harness;

namespace RuBackend.Harness.Reconciliation;

public sealed record RecoveredRunSummary(RunId RunId, string Action);

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
    Func<CancellationToken, Task<IReadOnlyList<RunId>>>? pendingRunSource = null)
{
    public async Task<IReadOnlyList<RecoveredRunSummary>> ScanPendingRunsAsync(
        CancellationToken cancellationToken)
    {
        var summaries = new List<RecoveredRunSummary>();
        var runIds = pendingRunSource is { } source
            ? await source(cancellationToken).ConfigureAwait(false)
            : Array.Empty<RunId>();
        foreach (var runId in runIds)
        {
            var summary = await ScanRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (summary is { } recovered)
            {
                summaries.Add(recovered);
            }
        }

        return summaries;
    }

    public async Task<RecoveredRunSummary?> ScanRunAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.State != HarnessRunState.Running)
        {
            return null;
        }

        var requested = new List<(ExecutionId ExecutionId, string? SafeCode)>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var envelope in store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var item = envelope.Event;
            if (item.Kind == "tool.execution_requested" && item.ExecutionId is { } executionId)
            {
                requested.Add((executionId, item.SafeCode));
            }
            else if (item.Kind == "tool.execution_completed"
                     && item.ExecutionId is { } completedId)
            {
                _ = completed.Add(completedId.Value);
            }
        }

        var incomplete = requested
            .Where(item => !completed.Contains(item.ExecutionId.Value))
            .ToList();

        if (requested.Count == 0)
        {
            // The process died before any tool dispatch: no effects are possible.
            return await FailRunAsync(run, "recovery_pre_dispatch", cancellationToken).ConfigureAwait(false);
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
            terminalErrorCode);
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
                _ => "failed_pre_dispatch",
            })
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
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly ILogger<HarnessRecoveryStartupService> _logger;
    private readonly WorkspaceTrustService? _trustService;
    private readonly SqliteWorkspaceStore? _workspaceStore;
    private readonly HarnessPolicySnapshotService? _policySnapshots;
    private readonly IAgentRuntimeRootRegistry? _runtimeRoots;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;

    public HarnessRecoveryStartupService(
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        ILogger<HarnessRecoveryStartupService> logger,
        WorkspaceTrustService? trustService = null,
        SqliteWorkspaceStore? workspaceStore = null,
        HarnessPolicySnapshotService? policySnapshots = null,
        IAgentRuntimeRootRegistry? runtimeRoots = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null)
    {
        _store = store;
        _sqliteOptions = sqliteOptions;
        _logger = logger;
        _trustService = trustService;
        _workspaceStore = workspaceStore;
        _policySnapshots = policySnapshots;
        _runtimeRoots = runtimeRoots;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var pendingRunIds = await ListNonTerminalRunIdsAsync(stoppingToken).ConfigureAwait(false);
            var scanner = new HarnessRecoveryScanner(
                _store,
                new SystemTimeProvider(),
                _ => Task.FromResult<IReadOnlyList<RunId>>(pendingRunIds));
            var recovered = await scanner.ScanPendingRunsAsync(stoppingToken).ConfigureAwait(false);
            foreach (var summary in recovered)
            {
                _logger.LogWarning(
                    "startup recovery marked run {RunId} as {Action}",
                    summary.RunId.Value,
                    summary.Action);
            }

            if (_workspaceCoordinatorStore is not null)
            {
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
            }

            await RehydrateRuntimeRootsAsync(pendingRunIds, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogError(error, "startup recovery scan failed");
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
                if (run is null || run.State.IsTerminal() || run.State == HarnessRunState.ReconciliationRequired)
                {
                    continue;
                }

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
            catch (Exception error) when (
                error is HarnessStoreException
                    or IOException
                    or UnauthorizedAccessException
                    or CryptographicException)
            {
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
