using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Subagents;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentRunLifecycleService
{
    private readonly IHarnessEventStore _store;
    private readonly IAgentRuntimeRootRegistry _runtimeRoots;
    private readonly ReconciliationInspectionGate _inspectionGate;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly AgentCommandsService _commands;
    private readonly SubagentCancellationCoordinator _subagents;

    public AgentRunLifecycleService(IHarnessEventStore store, IAgentRuntimeRootRegistry runtimeRoots,
        ReconciliationInspectionGate inspectionGate, IIdGenerator ids, ITimeProvider time,
        AgentV1SubagentGuard subagentGuard, AgentCommandsService commands,
        SubagentCancellationCoordinator subagents)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtimeRoots = runtimeRoots ?? throw new ArgumentNullException(nameof(runtimeRoots));
        _inspectionGate = inspectionGate ?? throw new ArgumentNullException(nameof(inspectionGate));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _subagents = subagents ?? throw new ArgumentNullException(nameof(subagents));
    }
    public async Task<AgentServiceResult> CancelAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (await _subagents.CancelFamilyAsync(runId, cancellationToken).ConfigureAwait(false))
            run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ?? run;

        if (run.State.IsTerminal())
        {
            return new AgentServiceResult(200, AgentRunQueryService.MapRun(run));
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Cancel);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Cancel rejected.");
        }

        // Stop live terminal processes before committing the durable cancelled
        // projection. The runtime registry points at the same per-run coordinator
        // that owns the gateway/Job Object; merely changing SQLite state leaves a
        // child process running. The transition is validated first so a rejected
        // cancel cannot kill a healthy run's tools.
        await _runtimeRoots.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now, terminalErrorCode: "cancelled");

        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, AgentRunQueryService.MapRun(result.Run!));
    }

    /// <summary>
    /// Applies the typed user reconciliation decision. The snapshot hash the client
    /// presents must equal this host's freshly computed workspace/Git hash; anything
    /// else fails closed with reconcile_stale and leaves the run untouched.
    /// </summary>

    public async Task<AgentServiceResult> ContinueAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }


        if (run.State == HarnessRunState.ReconciliationRequired)
        {
            // A run parked on an ambiguous tool outcome has exactly one way out:
            // a reconciliation decision. The desktop client has no reconcile
            // endpoint of its own, and Continue is the only affordance it offers
            // here, so Continue *is* that decision — resolve it as a continue with
            // inspection rather than bouncing the user with invalid_run_transition.
            return await _commands.ContinueReconciliationAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Continue);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Continue rejected.");
        }

        // An explicit user Continue overrides the inspection gate: the user has
        // seen the ambiguous outcome and chosen to proceed. The override is
        // audited beside the continue request and is applied only after the
        // durable commit succeeds, so a commit conflict cannot leave the
        // in-memory gate open while the durable state still says otherwise.
        var gateCleared = _inspectionGate.IsMutationDenied(runId);

        var now = _time.UtcNow;
        var (nextRun, commitEvents) = PlanContinueCommit(run, transition.Next, gateCleared, _ids, now);

        var commit = new HarnessCommit(runId, run.Version, nextRun, commitEvents, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        if (gateCleared)
        {
            _inspectionGate.RecordInspection(runId);
        }

        return new AgentServiceResult(200, AgentRunQueryService.MapRun(result.Run!));
    }

    internal static (AgentRun NextRun, HarnessEventIntent[] Events) PlanContinueCommit(
        AgentRun run,
        HarnessRunStatus nextStatus,
        bool clearGate,
        IIdGenerator ids,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(ids);
        var events = new List<HarnessEventIntent>
        {
            new(ids.NewEventId(), HarnessEventKind.ContinueRequested, safeCode: "continued"),
        };
        if (clearGate)
        {
            events.Add(new(ids.NewEventId(), HarnessEventKind.ReconciliationResolved, safeCode: "gate_cleared_by_user"));
        }

        var next = AdvanceRun(run, nextStatus, run.BudgetWindow + 1, 0, 0, events.Count, now);
        return (next, events.ToArray());
    }

}
