using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;

// Generic preparation payload handling: PreparedPayloads with kind RecoveryOpaque are kept byte-exact
// and encrypted as InternalOnly/Opaque via HarnessPayloadStore without file-specific branches.
// The Host gateway maps recovery references to Broker restore content without exposing bytes.

namespace RuAgent.Harness.Core;

/// <summary>
/// Implemented by gateways that own live tool processes. Lets the coordinator forward
/// run-scoped cancellation so in-flight terminal executions terminate their whole
/// process tree and return cancelled receipts instead of being abandoned.
/// </summary>
public interface IToolRunCancellation
{
    Task CancelRunAsync(RunId runId, CancellationToken cancellationToken);
}

public sealed class AgentRunCoordinator
{
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly AgentContextAssembler _contextAssembler;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactor _transientRedactor;
    private readonly AgentToolExecutionPipeline _tools;
    private readonly IToolRunCancellation? _cancellation;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly IModelContextLimitProvider? _contextLimit;
    private readonly IAgentActivitySink? _activitySink;

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
        IAgentActivitySink? activitySink = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextAssembler = contextAssembler
            ?? throw new ArgumentNullException(nameof(contextAssembler));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _transientRedactor = transientRedactor
            ?? PassthroughTransientContentRedactor.Instance;
        _contextLimit = contextLimit;
        _inspectionGate = inspectionGate;
        _activitySink = activitySink;
        _tools = new AgentToolExecutionPipeline(
            gateway ?? throw new ArgumentNullException(nameof(gateway)),
            store,
            ids,
            time,
            scheduler ?? throw new ArgumentNullException(nameof(scheduler)),
            frames,
            _transientRedactor,
            inspectionGate,
            activitySink);
        _cancellation = gateway as IToolRunCancellation;
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
        var modelStepId = _ids.NewModelStepId();

        var providerLimit = _contextLimit?.MaxPromptTokens(runId);
        if (providerLimit is { } limitTokens
            && ModelContextSizeEstimator.EstimateTokens(context) > limitTokens)
        {
            // Never silently drop earlier results: the bounded continuation summary is
            // stored as a private payload and referenced from a safe event. Continue can
            // retry, but while the assembled context still exceeds the true provider
            // limit the step stays blocked.
            var summary = ContinuationSummaryBuilder.Build(
                context,
                ContinuationSummaryBuilder.DefaultMaxChars);
            var summaryBytes = Encoding.UTF8.GetBytes(summary.Text);
            var summaryPayloadId = _ids.NewPayloadId();
            var summaryPayload = new HarnessPayloadIntent(
                summaryPayloadId,
                HarnessPayloadKind.ContinuationSummary,
                HarnessPayloadAudience.InternalOnly,
                HarnessPayloadEncoding.Utf8Text,
                Math.Max(summaryBytes.Length, 1),
                summaryBytes);
            var limitEvent = new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepStarted,
                modelStepId: modelStepId,
                payloadId: summaryPayloadId,
                safeCode: "context_limit_reached");
            var limitTransition = AgentRunStateMachine.TryTransition(
                run.Status,
                HarnessRunTransition.ExhaustBudget);
            if (!limitTransition.Accepted)
            {
                throw new InvalidOperationException(limitTransition.ErrorCode);
            }

            var limitedNext = run.Advance(
                limitTransition.Next,
                run.BudgetWindow,
                run.ModelStepsInWindow,
                run.ToolCallsInWindow,
                1,
                _time.UtcNow);
            run = await CommitAsync(
                    run,
                    limitedNext,
                    new[] { limitEvent },
                    new[] { summaryPayload },
                    Array.Empty<ConversationOutboxIntent>(),
                    cancellationToken)
                .ConfigureAwait(false);
            return AgentRunStepResult.Blocked(run, "context_limit_reached");
        }

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

        var thinkingStartedAt = _time.UtcNow;
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectModelStep(
                modelStepId,
                AgentActivityState.Started,
                safeCode: "in_progress"));

        var request = new ModelStepRequest(
            run.RunId,
            modelStepId,
            context,
            run.Preset,
            run.PolicySnapshotVersion,
            run.PolicySnapshotSha256);
        var droppedBefore = _frames.DroppedCount;
        var assistantStream = new TransientContentStream(
            _frames,
            _transientRedactor,
            run.RunId,
            TransientFrameKind.AssistantDelta,
            modelStepId: modelStepId);
        AssistantStep step;
        try
        {
            step = await _model.RunStepAsync(
                    request,
                    delta => PublishAssistantDelta(modelStepId, assistantStream, delta),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("model_step_returned_null");
        }
        catch (OperationCanceledException)
        {
            assistantStream.Complete();
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectModelStep(
                    modelStepId,
                    AgentActivityState.Failed,
                    ElapsedMilliseconds(thinkingStartedAt),
                    safeCode: "cancelled"));
            throw;
        }
        catch (Exception)
        {
            assistantStream.Complete();
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectModelStep(
                    modelStepId,
                    AgentActivityState.Failed,
                    ElapsedMilliseconds(thinkingStartedAt),
                    safeCode: "model_failed"));
            throw;
        }
        assistantStream.Complete();
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectModelStep(
                modelStepId,
                AgentActivityState.Completed,
                ElapsedMilliseconds(thinkingStartedAt),
                safeCode: "succeeded"));
        var modelDroppedCount = checked((int)(_frames.DroppedCount - droppedBefore));
        if (modelDroppedCount > 0)
        {
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectDropped(
                    modelStepId,
                    toolFrames: false,
                    droppedCount: modelDroppedCount));
        }

        if (step.ToolCalls.Count > 0)
        {
            return await HandleToolStepAsync(
                    run,
                    modelStepId,
                    step,
                    modelDroppedCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await HandleFinalStepAsync(
                run,
                modelStepId,
                step,
                modelDroppedCount,
                cancellationToken)
            .ConfigureAwait(false);
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

    private async Task<AgentRunStepResult> HandleToolStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int modelDroppedCount,
        CancellationToken cancellationToken)
    {
        var reservation = AgentBudgetPolicy.ReserveToolCalls(
            run,
            step.ToolCalls.Count,
            _time.UtcNow);
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayloadId = _ids.NewPayloadId();
        var modelPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient
                | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);

        run = await CommitAsync(
                run,
                reservation.NextRun,
                new[]
                {
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        reservation.Granted
                            ? HarnessEventKind.ModelStepCompleted
                            : HarnessEventKind.ContinueRequested,
                        modelStepId: modelStepId,
                        payloadId: assistantPayloadId,
                        safeCount: step.ToolCalls.Count,
                        safeCode: reservation.ErrorCode),
                },
                new[] { modelPayload },
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!reservation.Granted)
        {
            return AgentRunStepResult.Blocked(run, reservation.ErrorCode!);
        }

        var result = await _tools.ExecuteAsync(
                run,
                modelStepId,
                step.ToolCalls,
                modelDroppedCount,
                cancellationToken)
            .ConfigureAwait(false);
        return new AgentRunStepResult(result.Run, result.Receipts, result.ErrorCode);
    }

    private async Task<AgentRunStepResult> HandleFinalStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int droppedCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(step.Text))
        {
            throw new InvalidOperationException("model_final_text_missing");
        }

        var stateChange = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.BeginFinalHistory);
        if (!stateChange.Accepted)
        {
            throw new InvalidOperationException(stateChange.ErrorCode);
        }

        var payloadId = _ids.NewPayloadId();
        var bytes = Encoding.UTF8.GetBytes(step.Text);
        var payload = new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(bytes.Length, 1),
            bytes);
        var events = new List<HarnessEventIntent>
        {
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: payloadId),
        };
        if (droppedCount > 0)
        {
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.TransientFramesDropped,
                modelStepId: modelStepId,
                safeCount: droppedCount));
        }

        var next = run.Advance(
            stateChange.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        run = await CommitAsync(
                run,
                next,
                events,
                new[] { payload },
                new[]
                {
                    new ConversationOutboxIntent(
                        _ids.NewEventId(),
                        run.SessionId,
                        payloadId,
                        ConversationOutboxRole.Assistant,
                        HistoryWaitKind.FinalBeforeCompletion),
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new AgentRunStepResult(run, Array.Empty<ToolExecutionReceipt>());
    }

    private static ValueTask PublishAssistantDelta(
        ModelStepId expectedStepId,
        TransientContentStream stream,
        AssistantDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.ModelStepId != expectedStepId)
        {
            throw new InvalidOperationException("model_delta_identity_mismatch");
        }

        stream.Append(delta.Content);
        return ValueTask.CompletedTask;
    }

    private int? ElapsedMilliseconds(DateTimeOffset startedAt)
    {
        var elapsed = _time.UtcNow - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            return null;
        }

        var milliseconds = elapsed.TotalMilliseconds;
        return milliseconds is >= 0 and <= 86_400_000
            ? (int)milliseconds
            : null;
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
