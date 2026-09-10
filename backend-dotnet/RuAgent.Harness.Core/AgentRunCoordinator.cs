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
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly AgentContextAssembler _contextAssembler;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactor _transientRedactor;
    private readonly AgentToolExecutionPipeline _tools;
    private readonly AgentRunCompaction _compaction;
    private readonly AgentRunLifecycle _lifecycle;
    private readonly ISystemPromptContextProvider _promptContextProvider;
    private readonly IAgentActivitySink? _activitySink;
    private readonly IAgentReplyPolicy _replyPolicy;
    private readonly IAgentReplyEffectSink _replyEffects;

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
        ContextBudgetPolicy? contextBudgetPolicy = null)
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
        _activitySink = activitySink;
        _replyPolicy = replyPolicy ?? new PassthroughAgentReplyPolicy();
        _replyEffects = replyEffects ?? new NoopAgentReplyEffectSink();
        _promptContextProvider = promptContextProvider
            ?? throw new ArgumentNullException(nameof(promptContextProvider));
        _compaction = new AgentRunCompaction(
            _store,
            _contextAssembler,
            _ids,
            _time,
            contextLimit,
            contextBudgetPolicy ?? new ContextBudgetPolicy());
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
        _lifecycle = new AgentRunLifecycle(
            _store,
            _ids,
            _time,
            inspectionGate,
            gateway as IToolRunCancellation);
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

        var thinkingStartedAt = _time.UtcNow;
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectModelStep(
                modelStepId,
                AgentActivityState.Started,
                safeCode: "in_progress"));

        SystemPromptContext promptContext;
        try
        {
            promptContext = await _promptContextProvider
                .BuildAsync(run, context, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("prompt_context_null");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt)
                .ConfigureAwait(false);
            throw new InvalidOperationException("prompt_contract_invalid");
        }

        var request = new ModelStepRequest(
            run.RunId,
            modelStepId,
            context,
            promptContext,
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
        ModelStepResult modelResult;
        try
        {
            modelResult = await _model.RunStepAsync(
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            modelResult.PromptReceipt.ValidateAgainst(promptContext);
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
        catch (ArgumentException)
        {
            assistantStream.Complete();
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt)
                .ConfigureAwait(false);
            throw new InvalidOperationException("prompt_contract_invalid");
        }

        HarnessPayloadIntent promptReceiptPayload;
        try
        {
            var receiptBytes = PromptReceiptPayloadCodec.Serialize(modelResult.PromptReceipt);
            promptReceiptPayload = new HarnessPayloadIntent(
                _ids.NewPayloadId(),
                HarnessPayloadKind.PromptReceipt,
                HarnessPayloadAudience.InternalOnly,
                HarnessPayloadEncoding.Json,
                PromptReceiptPayloadCodec.MaximumBytes,
                receiptBytes);
        }
        catch (Exception)
        {
            assistantStream.Complete();
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt)
                .ConfigureAwait(false);
            throw new InvalidOperationException("prompt_contract_invalid");
        }

        var promptComposedEvent = new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.ModelPromptComposed,
            modelStepId: modelStepId,
            payloadId: promptReceiptPayload.PayloadId,
            safeCode: "composed");
        var step = modelResult.Step;
        if (step.Usage is null && modelResult.Usage is not null)
        {
            step = new AssistantStep(
                step.Text,
                step.ToolCalls,
                step.FinishReason,
                step.EstimatedInputTokens,
                modelResult.Usage);
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
            if (ConsecutiveToolCallGuard.IsRepeated(context, step.ToolCalls))
            {
                return await HandleRepeatedToolStepAsync(
                        run,
                        modelStepId,
                        step,
                        promptReceiptPayload,
                        promptComposedEvent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await HandleToolStepAsync(
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
            return await HandleUnverifiedFailureStepAsync(
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

        return await HandleFinalStepAsync(
                run,
                modelStepId,
                step,
                modelDroppedCount,
                promptReceiptPayload,
                promptComposedEvent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentRunStepResult> HandleUnverifiedFailureStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        string errorCode,
        int droppedCount,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken)
    {
        const string stepErrorCode = "unverified_after_tool_failure";
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Fail);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);
        var events = new List<HarnessEventIntent>
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCode: stepErrorCode),
        };
        if (droppedCount > 0)
        {
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.TransientFramesDropped,
                modelStepId: modelStepId,
                safeCount: droppedCount));
        }
        events.Add(new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.RunFailed,
            safeCode: errorCode));

        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow,
            errorCode);
        var committed = await CommitAsync(
                run,
                next,
                events,
                [promptReceiptPayload, assistantPayload],
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        return AgentRunStepResult.Blocked(committed, errorCode);
    }

    private async Task<AgentRunStepResult> HandleRepeatedToolStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken)
    {
        const string errorCode = "repeated_tool_call";
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Fail);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient
                | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);
        var events = new HarnessEventIntent[]
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCount: step.ToolCalls.Count,
                safeCode: errorCode),
            new(
                _ids.NewEventId(),
                HarnessEventKind.RunFailed,
                safeCode: errorCode),
        };
        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Length,
            _time.UtcNow,
            errorCode);
        var committed = await CommitAsync(
                run,
                next,
                events,
                [promptReceiptPayload, assistantPayload],
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        return AgentRunStepResult.Blocked(committed, errorCode);
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

    private async Task<AgentRunStepResult> HandleToolStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int modelDroppedCount,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
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

        // ReserveToolCalls computes counters for a one-event commit. The prompt
        // receipt is persisted beside the model completion, so advance the same
        // counters with the complete two-event write set.
        var reservationNext = run.Advance(
            reservation.NextRun.Status,
            reservation.NextRun.BudgetWindow,
            reservation.NextRun.ModelStepsInWindow,
            reservation.NextRun.ToolCallsInWindow,
            appendedEventCount: 2,
            updatedAt: _time.UtcNow,
            terminalErrorCode: reservation.NextRun.TerminalErrorCode);
        run = await CommitAsync(
                run,
                reservationNext,
                new[]
                {
                    promptComposedEvent,
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
                new[] { promptReceiptPayload, modelPayload },
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
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
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

        var reply = await _replyPolicy
            .PrepareAsync(run, step.Text, cancellationToken)
            .ConfigureAwait(false);
        var payloadId = _ids.NewPayloadId();
        var bytes = Encoding.UTF8.GetBytes(reply.VisibleText);
        var payload = new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(bytes.Length, 1),
            bytes);
        var payloads = new List<HarnessPayloadIntent> { promptReceiptPayload, payload };
        PayloadId? usagePayloadId = null;
        if (step.Usage is not null)
        {
            var usageBytes = SerializeUsage(step.Usage);
            usagePayloadId = _ids.NewPayloadId();
            payloads.Add(new HarnessPayloadIntent(
                usagePayloadId.Value,
                HarnessPayloadKind.ModelUsage,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                Math.Max(usageBytes.Length, 1),
                usageBytes));
        }
        var events = new List<HarnessEventIntent>
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: payloadId),
        };
        if (usagePayloadId is { } usageId)
        {
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ModelUsageRecorded,
                modelStepId: modelStepId,
                payloadId: usageId,
                safeCode: "reported"));
        }
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
                payloads,
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
        try
        {
            await _replyEffects
                .ApplyAsync(run, reply, modelStepId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectModelStep(
                    modelStepId,
                    AgentActivityState.Failed,
                    safeCode: "reply_effect_failed"));
        }
        return new AgentRunStepResult(run, Array.Empty<ToolExecutionReceipt>());
    }

    private static byte[] SerializeUsage(ModelUsage usage)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            usage.WriteTo(writer);
        }

        return stream.ToArray();
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

    private async Task MarkPromptContractFailureAsync(
        AgentRun run,
        ModelStepId modelStepId,
        DateTimeOffset startedAt)
    {
        await _lifecycle.FailAsync(
                run.RunId,
                "prompt_contract_invalid",
                CancellationToken.None)
            .ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectModelStep(
                modelStepId,
                AgentActivityState.Failed,
                ElapsedMilliseconds(startedAt),
                safeCode: "prompt_contract_invalid"));
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

}
