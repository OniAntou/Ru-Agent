using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Approval;
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
    private readonly Func<RunId, CancellationToken, Task>? _approvalResolutionRaceHook;

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
        IRetainedApprovalStash? retainedStash = null,
        Func<RunId, CancellationToken, Task>? approvalResolutionRaceHook = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retainedStash = retainedStash ?? new InMemoryRetainedApprovalStash(time);
        _approvalResolutionRaceHook = approvalResolutionRaceHook;
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
            contextBudgetPolicy ?? CreateBudgetPolicy(),
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

    /// <summary>
    /// Builds a <see cref="ContextBudgetPolicy"/> from settings compaction ratios,
    /// falling back to <see cref="ContextBudgetPolicyOptions"/> defaults so every
    /// call site keeps working and an invalid pair can never break the run
    /// (fail-closed to defaults). Spec: 0 &lt; target &lt; trigger &lt; 1.
    /// </summary>
    public static ContextBudgetPolicy CreateBudgetPolicy(
        double triggerRatio = ContextBudgetPolicyOptions.DefaultNormalTriggerRatio,
        double targetRatio = ContextBudgetPolicyOptions.DefaultNormalTargetRatio)
    {
        if (!ContextBudgetPolicyOptions.IsValidRatio(triggerRatio)
            || !ContextBudgetPolicyOptions.IsValidRatio(targetRatio)
            || !(targetRatio < triggerRatio)
            || triggerRatio >= 1)
        {
            return new ContextBudgetPolicy();
        }

        try
        {
            return new ContextBudgetPolicy(new ContextBudgetPolicyOptions(
                NormalTriggerRatio: triggerRatio,
                NormalTargetRatio: targetRatio));
        }
        catch (ArgumentOutOfRangeException)
        {
            return new ContextBudgetPolicy();
        }
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
        var reasoningText = invocation.ReasoningText;

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
                        cancellationToken,
                        reasoningText)
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
                        cancellationToken,
                        reasoningText)
                    .ConfigureAwait(false);
            }

            return await _steps.HandleToolStepAsync(
                    run,
                    modelStepId,
                    step,
                    modelDroppedCount,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken,
                    reasoningText)
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
                    cancellationToken,
                    reasoningText)
                .ConfigureAwait(false);
        }

        return await _steps.HandleFinalStepAsync(
                run,
                modelStepId,
                step,
                modelDroppedCount,
                promptReceiptPayload,
                promptComposedEvent,
                cancellationToken,
                reasoningText)
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

    public void RestoreRetainedApproval(RunId runId, RetainedApprovalStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Proposals.Count == 0
            || step.Proposals.Any(proposal => proposal.RunId != runId))
        {
            throw new InvalidOperationException("retained_approval_run_mismatch");
        }

        if (_retainedStash.TryPeek(runId, out var existing)
            && !SameRetainedApproval(existing!, step))
        {
            throw new InvalidOperationException("retained_approval_stash_mismatch");
        }

        _retainedStash.Stash(runId, step);
    }

    public async Task<AgentRunStepResult?> TryResumeRetainedApprovalAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        AgentRun? activeRun = null;
        try
        {
            activeRun = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (activeRun.State != HarnessRunState.Running)
            {
                _retainedStash.Drop(runId);
                return null;
            }

            if (!_retainedStash.TryPeek(runId, out var retained) || retained is null)
            {
                return null;
            }

            var resolution = await GetApprovalResolutionAsync(
                    runId,
                    retained.ApprovalId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolution == ApprovalResolutionState.Expired)
            {
                _retainedStash.Drop(runId);
                try
                {
                    var failed = await _lifecycle
                        .FailAsync(runId, "approval_expired", CancellationToken.None)
                        .ConfigureAwait(false);
                    return AgentRunStepResult.Blocked(failed, "approval_expired");
                }
                catch
                {
                    return AgentRunStepResult.Blocked(
                        activeRun,
                        "approval_expired");
                }
            }

            if (resolution != ApprovalResolutionState.Resolved)
            {
                _retainedStash.Drop(runId);
                return null;
            }

            if (!_retainedStash.TryTake(runId, retained.ApprovalId, out var claimed) || claimed is null)
            {
                return null;
            }

            var result = await _tools.ResumeRetainedAsync(activeRun, claimed, cancellationToken)
                .ConfigureAwait(false);
            return new AgentRunStepResult(result.Run, result.Receipts, result.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _retainedStash.Drop(runId);
            if (activeRun is { State: HarnessRunState.Running })
            {
                try
                {
                    var failed = await _lifecycle
                        .FailAsync(runId, "retained_approval_resume_failed", CancellationToken.None)
                        .ConfigureAwait(false);
                    return AgentRunStepResult.Blocked(
                        failed,
                        "retained_approval_resume_failed");
                }
                catch
                {
                    return AgentRunStepResult.Blocked(
                        activeRun,
                        "retained_approval_resume_failed");
                }
            }

            return null;
        }
    }

    public async Task<AgentRunStepResult?> TryRefreshRetainedApprovalAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        AgentRun? activeRun = null;
        try
        {
            activeRun = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (activeRun.State != HarnessRunState.WaitingApproval)
            {
                return null;
            }

            if (_approvalResolutionRaceHook is { } raceHook)
            {
                await raceHook(runId, cancellationToken).ConfigureAwait(false);
                activeRun = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
                if (activeRun.State != HarnessRunState.WaitingApproval)
                {
                    return null;
                }
            }

            if (!_retainedStash.TryPeek(runId, out var retained) || retained is null)
            {
                return null;
            }

            var resolution = await GetApprovalResolutionAsync(
                    runId,
                    retained.ApprovalId,
                    cancellationToken)
                .ConfigureAwait(false);
            activeRun = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (activeRun.State != HarnessRunState.WaitingApproval)
            {
                return null;
            }

            if (resolution != ApprovalResolutionState.Pending)
            {
                if (resolution == ApprovalResolutionState.Expired)
                {
                    _retainedStash.Drop(runId);
                    try
                    {
                        var failed = await _lifecycle
                            .FailAsync(runId, "approval_expired", CancellationToken.None)
                            .ConfigureAwait(false);
                        return AgentRunStepResult.Blocked(failed, "approval_expired");
                    }
                    catch
                    {
                        return AgentRunStepResult.Blocked(activeRun, "approval_expired");
                    }
                }

                return null;
            }

            if (!_retainedStash.TryTake(runId, retained.ApprovalId, out var claimed)
                || claimed is null)
            {
                return null;
            }

            var result = await _tools
                .RefreshRetainedApprovalAsync(activeRun, claimed, cancellationToken)
                .ConfigureAwait(false);
            return new AgentRunStepResult(result.Run, result.Receipts, result.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var current = await _store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (current?.State == HarnessRunState.Running)
            {
                return null;
            }

            _retainedStash.Drop(runId);
            if (activeRun is { State: HarnessRunState.WaitingApproval })
            {
                try
                {
                    var failed = await _lifecycle
                        .FailAsync(runId, "retained_approval_recovery_failed", CancellationToken.None)
                        .ConfigureAwait(false);
                    return AgentRunStepResult.Blocked(
                        failed,
                        "retained_approval_recovery_failed");
                }
                catch
                {
                    return AgentRunStepResult.Blocked(
                        activeRun,
                        "retained_approval_recovery_failed");
                }
            }

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

    private async Task<ApprovalResolutionState> GetApprovalResolutionAsync(
        RunId runId,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        HarnessEvent? request = null;
        var resolved = false;
        var superseded = false;
        await foreach (var envelope in _store
            .ReadEventsAsync(runId, 0, cancellationToken)
            .ConfigureAwait(false))
        {
            var item = envelope.Event;
            if (string.Equals(item.Kind, "approval.requested", StringComparison.Ordinal)
                && item.ApprovalId is { } found
                && string.Equals(found.Value, approvalId.Value, StringComparison.Ordinal))
            {
                if (request is not null)
                {
                    throw new InvalidOperationException("approval_request_duplicate");
                }

                request = item;
                continue;
            }

            if (request is not null
                && string.Equals(item.Kind, "approval.requested", StringComparison.Ordinal))
            {
                superseded = true;
                continue;
            }

            if (!superseded
                && request is not null
                && item.Sequence > request.Sequence
                && string.Equals(item.Kind, "run.state_changed", StringComparison.Ordinal)
                && string.Equals(item.SafeCode, "approval_resolved", StringComparison.Ordinal))
            {
                resolved = true;
            }
        }

        if (request is null)
        {
            return ApprovalResolutionState.Missing;
        }

        return ApprovalState.IsExpired(request.OccurredAt, _time.UtcNow)
            ? ApprovalResolutionState.Expired
            : resolved
                ? ApprovalResolutionState.Resolved
                : ApprovalResolutionState.Pending;
    }

    private static bool SameRetainedApproval(
        RetainedApprovalStep left,
        RetainedApprovalStep right)
    {
        if (left.ApprovalId != right.ApprovalId
            || left.ModelStepId != right.ModelStepId
            || left.Proposals.Count != right.Proposals.Count)
        {
            return false;
        }

        return left.Proposals
            .OrderBy(item => item.ModelOrder)
            .Zip(
                right.Proposals.OrderBy(item => item.ModelOrder),
                (first, second) =>
                    first.RunId == second.RunId
                    && first.ModelStepId == second.ModelStepId
                    && first.ToolCallId == second.ToolCallId
                    && first.ProviderCallId == second.ProviderCallId
                    && first.ModelOrder == second.ModelOrder
                    && first.ToolId == second.ToolId
                    && first.ToolVersion == second.ToolVersion
                    && SameArguments(first.Arguments, second.Arguments))
            .All(matches => matches);
    }

    private static bool SameArguments(JsonElement left, JsonElement right)
    {
        try
        {
            return JsonNode.DeepEquals(
                JsonNode.Parse(left.GetRawText()),
                JsonNode.Parse(right.GetRawText()));
        }
        catch
        {
            return false;
        }
    }

    private enum ApprovalResolutionState
    {
        Missing,
        Pending,
        Resolved,
        Expired,
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
