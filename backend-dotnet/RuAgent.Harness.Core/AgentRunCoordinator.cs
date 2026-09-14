using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Reconciliation;

// Generic preparation payload handling: PreparedPayloads with kind RecoveryOpaque are kept byte-exact
// and encrypted as InternalOnly/Opaque via HarnessPayloadStore without file-specific branches.
// The Host gateway maps recovery references to Broker restore content without exposing bytes.

namespace RuAgent.Harness.Core;

public sealed class AgentRunCoordinator
{
    private readonly IHarnessEventStore _store;
    private readonly AgentContextAssembler _contextAssembler;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentToolExecutionPipeline _tools;
    private readonly AgentRunCompaction _compaction;
    private readonly AgentRunLifecycle _lifecycle;
    private readonly AgentRunStepHandlers _steps;
    private readonly AgentModelInvocation _invocation;
    private readonly IRetainedApprovalStash _retainedStash;

    public AgentRunCoordinator(
        IModelStepRunner model,
        IToolGateway gateway,
        IHarnessEventStore store,
        AgentContextAssembler contextAssembler,
        IIdGenerator ids,
        ITimeProvider time,
        ToolCallScheduler scheduler,
        TransientFrameBuffer frames,
        ITransientContentRedactor? transientRedactor = null,
        IModelContextLimitProvider? contextLimit = null,
        ReconciliationInspectionGate? inspectionGate = null,
        IAgentActivitySink? activitySink = null,
        IAgentReplyPolicy? replyPolicy = null,
        IAgentReplyEffectSink? replyEffects = null,
        ISystemPromptContextProvider? promptContextProvider = null,
        ContextBudgetPolicy? contextBudgetPolicy = null,
        IRetainedApprovalStash? retainedStash = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retainedStash = retainedStash ?? new InMemoryRetainedApprovalStash(time);
        _contextAssembler = contextAssembler
            ?? throw new ArgumentNullException(nameof(contextAssembler));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _compaction = new AgentRunCompaction(
            _store,
            _contextAssembler,
            _ids,
            _time,
            contextLimit,
            contextBudgetPolicy ?? new ContextBudgetPolicy(),
            activitySink);
        _tools = new AgentToolExecutionPipeline(
            gateway ?? throw new ArgumentNullException(nameof(gateway)),
            store,
            ids,
            time,
            scheduler ?? throw new ArgumentNullException(nameof(scheduler)),
            frames,
            transientRedactor ?? PassthroughTransientContentRedactor.Instance,
            inspectionGate,
            activitySink,
            _retainedStash);
        _lifecycle = new AgentRunLifecycle(
            _store,
            _ids,
            _time,
            inspectionGate,
            gateway as IToolRunCancellation);
        _steps = new AgentRunStepHandlers(
            _store,
            _ids,
            _time,
            replyPolicy ?? new PassthroughAgentReplyPolicy(),
            replyEffects ?? new NoopAgentReplyEffectSink(),
            activitySink,
            _tools);
        _invocation = new AgentModelInvocation(
            model ?? throw new ArgumentNullException(nameof(model)),
            promptContextProvider ?? throw new ArgumentNullException(nameof(promptContextProvider)),
            frames ?? throw new ArgumentNullException(nameof(frames)),
            transientRedactor ?? PassthroughTransientContentRedactor.Instance,
            activitySink,
            _ids,
            _time,
            _lifecycle);
    }

    public async Task<AgentRunStepResult> RunOneStepAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.State != HarnessRunState.Running)
        {
            return AgentRunStepResult.Blocked(run, "run_not_runnable");
        }

        var context = await _contextAssembler
            .AssembleAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        var preflight = await _compaction.PreflightContextAsync(
                run,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        run = preflight.Run ?? run;
        if (preflight.ErrorCode is not null)
        {
            if (preflight.ErrorCode == "context_limit_reached")
            {
                var limitModelStepId = _ids.NewModelStepId();
                run = await CommitContextLimitAsync(
                        run,
                        limitModelStepId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return AgentRunStepResult.Blocked(run, "context_limit_reached");
            }

            return AgentRunStepResult.Blocked(
                preflight.Run ?? run,
                preflight.ErrorCode);
        }

        context = preflight.Context ?? context;
        var modelStepId = _ids.NewModelStepId();

        var reservation = AgentBudgetPolicy.ReserveModelStep(run, _time.UtcNow);
        var startEvent = new HarnessEventIntent(
            _ids.NewEventId(),
            reservation.Granted
                ? HarnessEventKind.ModelStepStarted
                : HarnessEventKind.ContinueRequested,
            modelStepId: modelStepId,
            safeCode: reservation.ErrorCode);
        run = await CommitAsync(
                run,
                reservation.NextRun,
                new[] { startEvent },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
                .ConfigureAwait(false);
        if (!reservation.Granted)
        {
            return AgentRunStepResult.Blocked(run, reservation.ErrorCode!);
        }

        var invocation = await _invocation.InvokeAsync(
                run,
                context,
                modelStepId,
                cancellationToken)
            .ConfigureAwait(false);
        var step = invocation.Step;
        var promptReceiptPayload = invocation.PromptReceiptPayload;
        var promptComposedEvent = invocation.PromptComposedEvent;
        var modelDroppedCount = invocation.ModelDroppedCount;

        if (step.ToolCalls.Count > 0)
        {
            var repetition = ConsecutiveToolCallGuard.Inspect(
                context,
                step.ToolCalls,
                out var receiptCode);
            if (repetition == RepetitionGuardVerdict.Advise)
            {
                return await _steps.HandleRepetitionAdviceStepAsync(
                        run,
                        modelStepId,
                        step,
                        receiptCode,
                        modelDroppedCount,
                        promptReceiptPayload,
                        promptComposedEvent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (repetition == RepetitionGuardVerdict.Stop)
            {
                return await _steps.HandleRepeatedToolStepAsync(
                        run,
                        modelStepId,
                        step,
                        promptReceiptPayload,
                        promptComposedEvent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _steps.HandleToolStepAsync(
                    run,
                    modelStepId,
                    step,
                    modelDroppedCount,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (FailedToolCompletionGuard.TryGetFailure(context, out var failureCode))
        {
            return await _steps.HandleUnverifiedFailureStepAsync(
                    run,
                    modelStepId,
                    step,
                    failureCode,
                    modelDroppedCount,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _steps.HandleFinalStepAsync(
                run,
                modelStepId,
                step,
                modelDroppedCount,
                promptReceiptPayload,
                promptComposedEvent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<AgentRun> AcknowledgeHistoryAsync(
        RunId runId,
        HistoryWaitKind historyWaitKind,
        CancellationToken cancellationToken) =>
        _lifecycle.AcknowledgeHistoryAsync(runId, historyWaitKind, cancellationToken);

    public Task<AgentRun> FailAsync(
        RunId runId,
        string errorCode,
        CancellationToken cancellationToken) =>
        _lifecycle.FailAsync(runId, errorCode, cancellationToken);

    public Task<AgentRun> ContinueAsync(
        RunId runId,
        CancellationToken cancellationToken) =>
        _lifecycle.ContinueAsync(runId, cancellationToken);

    public Task<ContextCompactionResult> CompactAsync(
        RunId runId,
        ContextCompactionStrategy strategy,
        ContextCompactionReason reason,
        CancellationToken cancellationToken) =>
        _compaction.CompactAsync(runId, strategy, reason, cancellationToken);

    /// <summary>
    /// Moves a Running run to WaitingApproval without consuming any budget counter.
    /// </summary>
    public Task<AgentRun> EnterApprovalWaitAsync(
        RunId runId,
        ApprovalId approvalId,
        CancellationToken cancellationToken) =>
        _lifecycle.EnterApprovalWaitAsync(runId, approvalId, cancellationToken);

    /// <summary>
    /// Returns a WaitingApproval run to Running after an approve/deny/typed-expiry
    /// decision. Budget counters remain untouched: waits never consume budget.
    /// </summary>
    public Task<AgentRun> ResumeAfterApprovalAsync(
        RunId runId,
        CancellationToken cancellationToken) =>
        _lifecycle.ResumeAfterApprovalAsync(runId, cancellationToken);

    /// <summary>
    /// Executes proposals parked by an earlier approval wait without a new model
    /// step. Only resumes when the durable stream proves this exact approval was
    /// resolved; anything else drops the stash and reports nothing to resume, so
    /// a stale or foreign approval can never execute parked calls.
    /// </summary>
    public async Task<AgentRunStepResult?> TryResumeRetainedApprovalAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run.State != HarnessRunState.Running)
            {
                _retainedStash.Drop(runId);
                return null;
            }

            if (!_retainedStash.TryPeek(runId, out var retained) || retained is null)
            {
                return null;
            }

            var resolved = await HasApprovalResolutionAsync(runId, retained.ApprovalId, cancellationToken)
                .ConfigureAwait(false);
            if (!resolved)
            {
                _retainedStash.Drop(runId);
                return null;
            }

            if (!_retainedStash.TryTake(runId, retained.ApprovalId, out var claimed) || claimed is null)
            {
                return null;
            }

            var result = await _tools.ResumeRetainedAsync(run, claimed, cancellationToken).ConfigureAwait(false);
            return new AgentRunStepResult(result.Run, result.Receipts, result.ErrorCode);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _retainedStash.Drop(runId);
            return null;
        }
    }

    /// <summary>
    /// Forwards run-scoped cancellation to the gateway so in-flight terminal processes
    /// are killed through their Job Object. Safe to call when nothing is running.
    /// </summary>
    public Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken) =>
        _lifecycle.CancelRunningToolsAsync(runId, cancellationToken);

    /// <summary>
    /// Applies the typed user reconciliation decision to a run in the
    /// ReconciliationRequired state. Applied/NotApplied resolve directly; UnknownContinue
    /// resumes evaluation while the mutation gate denies non-read-only calls until one
    /// durable read-only inspection receipt is committed.
    /// </summary>
    public Task<AgentRun> ApplyReconciliationAsync(
        RunId runId,
        ReconciliationCommand command,
        CancellationToken cancellationToken) =>
        _lifecycle.ApplyReconciliationAsync(runId, command, cancellationToken);

    private async Task<AgentRun> CommitContextLimitAsync(
        AgentRun run,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ExhaustBudget);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            appendedEventCount: 1,
            updatedAt: _time.UtcNow);
        return await CommitAsync(
                run,
                next,
                [new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ModelStepStarted,
                    modelStepId: modelStepId,
                    safeCode: "context_limit_reached")],
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

    private async Task<bool> HasApprovalResolutionAsync(
        RunId runId,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var requestSequence = 0L;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var evt = envelope.Event;
            if (requestSequence == 0
                && string.Equals(evt.Kind, "approval.requested", StringComparison.Ordinal)
                && evt.ApprovalId is { } found
                && string.Equals(found.Value, approvalId.Value, StringComparison.Ordinal))
            {
                requestSequence = evt.Sequence;
            }
            else if (requestSequence > 0
                && string.Equals(evt.Kind, "run.state_changed", StringComparison.Ordinal)
                && string.Equals(evt.SafeCode, "approval_resolved", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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

}
