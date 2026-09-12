using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core;

/// <summary>
/// Finalizes a model step: tool-call, final-text, repeated-call and unverified-failure
/// branches. Moved verbatim from AgentRunCoordinator; the orchestrator
/// (RunOneStepAsync) keeps the branching decision and delegates here.
/// </summary>
internal sealed class AgentRunStepHandlers
{
    private readonly IHarnessEventStore _store;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly IAgentReplyPolicy _replyPolicy;
    private readonly IAgentReplyEffectSink _replyEffects;
    private readonly IAgentActivitySink? _activitySink;
    private readonly AgentToolExecutionPipeline _tools;

    public AgentRunStepHandlers(
        IHarnessEventStore store,
        IIdGenerator ids,
        ITimeProvider time,
        IAgentReplyPolicy replyPolicy,
        IAgentReplyEffectSink replyEffects,
        IAgentActivitySink? activitySink,
        AgentToolExecutionPipeline tools)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _replyPolicy = replyPolicy ?? throw new ArgumentNullException(nameof(replyPolicy));
        _replyEffects = replyEffects ?? throw new ArgumentNullException(nameof(replyEffects));
        _activitySink = activitySink;
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public async Task<AgentRunStepResult> HandleUnverifiedFailureStepAsync(
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

    public async Task<AgentRunStepResult> HandleRepeatedToolStepAsync(
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

    /// <summary>
    /// A final step can arrive with neither text nor tool calls when the model
    /// returns an empty completion. Fail closed with a precise bounded code so
    /// the run reports a real failure instead of an unhandled internal error.
    /// </summary>
    private async Task<AgentRunStepResult> FailEmptyFinalStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken)
    {
        const string errorCode = "empty_model_reply";
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

    public async Task<AgentRunStepResult> HandleToolStepAsync(
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

    public async Task<AgentRunStepResult> HandleFinalStepAsync(
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
            return await FailEmptyFinalStepAsync(
                    run,
                    modelStepId,
                    step,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken)
                .ConfigureAwait(false);
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
