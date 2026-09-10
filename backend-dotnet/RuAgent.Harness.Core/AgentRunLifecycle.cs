using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;

namespace RuAgent.Harness.Core;

/// <summary>
/// Thin run-state transitions (acknowledge, fail, continue, approval waits,
/// reconciliation decisions, tool cancellation). Moved verbatim from
/// AgentRunCoordinator; every method is RequireRun -&gt; policy/state-machine -&gt;
/// Commit, never consuming budget except through AgentBudgetPolicy.
/// </summary>
internal sealed class AgentRunLifecycle
{
    private readonly IHarnessEventStore _store;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly IToolRunCancellation? _cancellation;

    public AgentRunLifecycle(
        IHarnessEventStore store,
        IIdGenerator ids,
        ITimeProvider time,
        ReconciliationInspectionGate? inspectionGate,
        IToolRunCancellation? cancellation)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _inspectionGate = inspectionGate;
        _cancellation = cancellation;
    }

    public async Task<AgentRun> AcknowledgeHistoryAsync(
        RunId runId,
        HistoryWaitKind historyWaitKind,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.State != HarnessRunState.WaitingHistory
            || run.HistoryWaitKind != historyWaitKind)
        {
            throw new InvalidOperationException("history_wait_mismatch");
        }

        var transition = historyWaitKind == HistoryWaitKind.UserBeforeFirstStep
            ? HarnessRunTransition.AcknowledgeUserHistory
            : HarnessRunTransition.AcknowledgeFinalHistory;
        var stateChange = AgentRunStateMachine.TryTransition(run.Status, transition);
        if (!stateChange.Accepted)
        {
            throw new InvalidOperationException(stateChange.ErrorCode);
        }

        var next = run.Advance(
            stateChange.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            _time.UtcNow);
        return await CommitAsync(
                run,
                next,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.RunStateChanged,
                        safeCode: StateCode(next.State)),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRun> FailAsync(
        RunId runId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var stateChange = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Fail);
        if (!stateChange.Accepted)
        {
            throw new InvalidOperationException(stateChange.ErrorCode);
        }

        var failure = new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.RunFailed,
            safeCode: errorCode);
        var next = run.Advance(
            stateChange.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            _time.UtcNow,
            errorCode);
        return await CommitAsync(
                run,
                next,
                new[] { failure },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRun> ContinueAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var next = AgentBudgetPolicy.Continue(run, _time.UtcNow);
        return await CommitAsync(
                run,
                next,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.ContinueRequested,
                        safeCode: "continued"),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a Running run to WaitingApproval without consuming any budget counter.
    /// </summary>
    public async Task<AgentRun> EnterApprovalWaitAsync(
        RunId runId,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var waiting = AgentBudgetPolicy.EnterApprovalWait(run, _time.UtcNow);
        return await CommitAsync(
                run,
                waiting,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.ApprovalRequested,
                        approvalId: approvalId,
                        safeCode: "approval_required"),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a WaitingApproval run to Running after an approve/deny/typed-expiry
    /// decision. Budget counters remain untouched: waits never consume budget.
    /// </summary>
    public async Task<AgentRun> ResumeAfterApprovalAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var resumed = AgentBudgetPolicy.ResumeAfterApproval(run, _time.UtcNow);
        return await CommitAsync(
                run,
                resumed,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.RunStateChanged,
                        safeCode: "approval_resolved"),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards run-scoped cancellation to the gateway so in-flight terminal processes
    /// are killed through their Job Object. Safe to call when nothing is running.
    /// </summary>
    public Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken) =>
        _cancellation?.CancelRunAsync(runId, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Applies the typed user reconciliation decision to a run in the
    /// ReconciliationRequired state. Applied/NotApplied resolve directly; UnknownContinue
    /// resumes evaluation while the mutation gate denies non-read-only calls until one
    /// durable read-only inspection receipt is committed.
    /// </summary>
    public async Task<AgentRun> ApplyReconciliationAsync(
        RunId runId,
        ReconciliationCommand command,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.State != HarnessRunState.ReconciliationRequired)
        {
            throw new InvalidOperationException("invalid_run_state");
        }

        var stateChange = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ApplyReconciliation);
        if (!stateChange.Accepted)
        {
            throw new InvalidOperationException(stateChange.ErrorCode);
        }

        if (command.Kind == ReconciliationCommandKind.UnknownContinue)
        {
            _inspectionGate?.Activate(runId);
        }

        var next = run.Advance(
            stateChange.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            _time.UtcNow);
        return await CommitAsync(
                run,
                next,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.ReconciliationResolved,
                        safeCode: command.ToWireToken()),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentRun> RequireRunAsync(
        RunId runId,
        CancellationToken cancellationToken) =>
        await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("run_not_found");

    private async Task<AgentRun> CommitAsync(
        AgentRun current,
        AgentRun next,
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> outbox,
        CancellationToken cancellationToken)
    {
        var result = await _store.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    events,
                    payloads,
                    outbox),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            HarnessCommitOutcome.Committed => result.Run!,
            HarnessCommitOutcome.VersionConflict =>
                throw new InvalidOperationException("run_version_conflict"),
            HarnessCommitOutcome.RunNotFound =>
                throw new KeyNotFoundException("run_not_found"),
            _ => throw new InvalidOperationException("unknown_commit_outcome"),
        };
    }

    private static string StateCode(HarnessRunState state) => state switch
    {
        HarnessRunState.Running => "running",
        HarnessRunState.Completed => "completed",
        _ => "state_changed",
    };
}
