using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

/// <summary>
/// Finalizes a model step: tool-call, final-text, repeated-call and unverified-failure
/// branches. Moved verbatim from AgentRunCoordinator; the orchestrator
/// (RunOneStepAsync) keeps the branching decision and delegates here.
/// </summary>
internal sealed partial class AgentRunStepHandlers
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
        ModelContextSnapshot context,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText = null)
    {
        const string stepErrorCode = "unverified_after_tool_failure";
        if (AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.UnverifiedAfterToolFailure))
        {
            return await HandleRecoveryAdviceStepAsync(
                    run,
                    modelStepId,
                    step,
                    droppedCount,
                    AgentRecoveryNotes.BuildUnverifiedNote(errorCode),
                    AgentRecoveryNotes.NoteCount(
                        context,
                        AgentRecoveryKind.UnverifiedAfterToolFailure),
                    stepErrorCode,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken,
                    reasoningText)
                .ConfigureAwait(false);
        }

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
            // Suppressed prose is audit-only: it must never reach the client.
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
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var commitPayloads = new List<HarnessPayloadIntent> { promptReceiptPayload, assistantPayload };
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            events.Add(reasoningEvent);
            commitPayloads.Add(reasoningPayload);
        }
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
                commitPayloads,
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        return AgentRunStepResult.Blocked(committed, errorCode);
    }

    /// <summary>
    /// Answers a dead-end model step (prose after a failed receipt, or an empty
    /// final step) with one durable recovery note instead of ending the run: the
    /// note lands in the model context as a system entry and the run keeps
    /// stepping. Bounded per case and per window by <see cref="AgentRecoveryNotes"/>;
    /// once the notes are spent the original fail branch takes over unchanged.
    /// </summary>
    private async Task<AgentRunStepResult> HandleRecoveryAdviceStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int droppedCount,
        string noteText,
        int priorNoteCount,
        string noteCode,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText)
    {
        var advisedCode = noteCode + "_advised";
        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            // The dead-end reply is audit-only: the recovery note replaces it in
            // the model context, and the user never sees the suppressed prose.
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);
        var notePayloadId = _ids.NewPayloadId();
        var noteBytes = Encoding.UTF8.GetBytes(noteText);
        var notePayload = new HarnessPayloadIntent(
            notePayloadId,
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            noteBytes.Length,
            noteBytes);
        var events = new List<HarnessEventIntent>
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCount: step.ToolCalls.Count,
                safeCode: advisedCode),
        };
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var commitPayloads = new List<HarnessPayloadIntent>
        {
            promptReceiptPayload,
            assistantPayload,
            notePayload,
        };
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            events.Add(reasoningEvent);
            commitPayloads.Add(reasoningPayload);
        }
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
            HarnessEventKind.RunNoteAdded,
            modelStepId: modelStepId,
            payloadId: notePayloadId,
            safeCode: noteCode));

        var next = run.Advance(
            run.Status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        var committed = await CommitAsync(
                run,
                next,
                events,
                commitPayloads,
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectRetry(modelStepId, priorNoteCount + 1));
        return new AgentRunStepResult(committed, Array.Empty<ToolExecutionReceipt>());
    }

    /// <summary>
    /// Answers a model step the runner rejected with a model-repairable code
    /// (malformed, unavailable or invalid tool call/selection) with one durable
    /// recovery note instead of ending the run. The runner threw before producing
    /// an assistant step, so the note is the whole commit; the run stays Running
    /// and the next step is the model's chance to re-issue a valid call. Bounded
    /// by <see cref="AgentRecoveryNotes"/>; once the notes are spent the
    /// coordinator's original fail path takes over.
    /// </summary>
    public async Task<AgentRunStepResult> HandleModelRepairableFailureStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        string errorCode,
        int priorNoteCount,
        CancellationToken cancellationToken)
    {
        var notePayloadId = _ids.NewPayloadId();
        var noteBytes = Encoding.UTF8.GetBytes(
            AgentRecoveryNotes.BuildModelRepairableNote(errorCode));
        var notePayload = new HarnessPayloadIntent(
            notePayloadId,
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            noteBytes.Length,
            noteBytes);
        var events = new List<HarnessEventIntent>
        {
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                safeCode: errorCode + "_advised"),
            new(
                _ids.NewEventId(),
                HarnessEventKind.RunNoteAdded,
                modelStepId: modelStepId,
                payloadId: notePayloadId,
                safeCode: errorCode),
        };

        var next = run.Advance(
            run.Status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        var committed = await CommitAsync(
                run,
                next,
                events,
                new[] { notePayload },
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectRetry(modelStepId, priorNoteCount + 1));
        return new AgentRunStepResult(committed, Array.Empty<ToolExecutionReceipt>());
    }

    /// <summary>
    /// Parks a run whose provider kept failing transiently until the runner's
    /// bounded retries were spent. Unlike a failure, the provider may recover
    /// while the user waits, so the run moves to WaitingUser with a
    /// ContinueRequested naming the provider code; an explicit Continue opens a
    /// fresh budget window and the loop retries a fresh model step. The runner
    /// threw before producing a step, so the park is the whole commit.
    /// </summary>
    public async Task<AgentRunStepResult> HandleProviderTransientPauseStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        HarnessRunStatus waitingStatus,
        string errorCode,
        CancellationToken cancellationToken)
    {
        const string parkCode = "waiting_user";
        var events = new List<HarnessEventIntent>
        {
            new(
                _ids.NewEventId(),
                HarnessEventKind.RunStateChanged,
                safeCode: parkCode),
            new(
                _ids.NewEventId(),
                HarnessEventKind.ContinueRequested,
                modelStepId: modelStepId,
                safeCode: errorCode),
        };
        var next = run.Advance(
            waitingStatus,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        var committed = await CommitAsync(
                run,
                next,
                events,
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectStatus(
                AgentActivityProjector.ActivityId(modelStepId.Value) + "_provider_paused",
                AgentActivityState.Waiting,
                errorCode,
                "Run paused: the provider kept failing transiently after retries. Send Continue to retry the model step."));
        return AgentRunStepResult.Blocked(committed, errorCode);
    }

    /// <summary>
    /// Answers a detected repetition with one corrective note instead of ending
    /// the run: the note lands in the model context as a system entry and the run
    /// keeps stepping, so a weak model can change its arguments. Bounded by
    /// <see cref="ConsecutiveToolCallGuard.MaximumRepairNotes"/>; once the notes
    /// are spent the stop branch takes over.
    /// </summary>
    public async Task<AgentRunStepResult> HandleRepetitionAdviceStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        string? receiptCode,
        int droppedCount,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText = null)
    {
        const string advisedCode = "repeated_tool_call_advised";
        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            // No tool was dispatched: retain the rejected calls for audit only.
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);
        var notePayloadId = _ids.NewPayloadId();
        var noteBytes = Encoding.UTF8.GetBytes(
            ConsecutiveToolCallGuard.BuildRepairNote(receiptCode));
        var notePayload = new HarnessPayloadIntent(
            notePayloadId,
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            noteBytes.Length,
            noteBytes);
        var events = new List<HarnessEventIntent>
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCount: step.ToolCalls.Count,
                safeCode: advisedCode),
        };
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var commitPayloads = new List<HarnessPayloadIntent> { promptReceiptPayload, assistantPayload, notePayload };
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            events.Add(reasoningEvent);
            commitPayloads.Add(reasoningPayload);
        }
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
            HarnessEventKind.RunNoteAdded,
            modelStepId: modelStepId,
            payloadId: notePayloadId,
            safeCode: "repeated_tool_call"));

        var next = run.Advance(
            run.Status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        var committed = await CommitAsync(
                run,
                next,
                events,
                commitPayloads,
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        return new AgentRunStepResult(committed, Array.Empty<ToolExecutionReceipt>());
    }

    /// <summary>
    /// The repetition survived the repair notes, so the run parks in WaitingUser
    /// with a bounded continue code instead of failing: the user can resume with
    /// an explicit Continue, exactly like a budget stop. A state that cannot park
    /// falls back to the previous fail-closed behavior.
    /// </summary>
    public async Task<AgentRunStepResult> HandleRepeatedToolStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText = null)
    {
        const string errorCode = "repeated_tool_call";
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ExhaustBudget);
        var failing = !transition.Accepted;
        if (failing)
        {
            transition = AgentRunStateMachine.TryTransition(
                run.Status,
                HarnessRunTransition.Fail);
            if (!transition.Accepted)
            {
                throw new InvalidOperationException(transition.ErrorCode);
            }
        }

        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            // No tool was dispatched: retain the rejected calls for audit only.
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
                safeCount: step.ToolCalls.Count,
                safeCode: errorCode),
            failing
                ? new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.RunFailed,
                    safeCode: errorCode)
                : new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ContinueRequested,
                    safeCode: errorCode),
        };
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var commitPayloads = new List<HarnessPayloadIntent> { promptReceiptPayload, assistantPayload };
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            events.Insert(2, reasoningEvent);
            commitPayloads.Add(reasoningPayload);
        }
        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow,
            failing ? errorCode : null);
        var committed = await CommitAsync(
                run,
                next,
                events,
                commitPayloads,
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
        CancellationToken cancellationToken,
        string? reasoningText = null,
        ProviderContinuation? providerContinuation = null)
    {
        var reservation = AgentBudgetPolicy.ReserveToolCalls(
            run,
            step.ToolCalls.Count,
            _time.UtcNow);
        if (!string.IsNullOrWhiteSpace(step.Text))
        {
            // The model said something before it reached for the tool. That sentence gets a
            // row of its own so a reader watching the run sees it as it happens, instead of
            // only finding out at the end what the run had been doing all along.
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectNarration(modelStepId, step.Text));
        }
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
        // counters with the complete write set below (two events, three when the
        // step recorded reasoning).
        var stepCompletedEvent = new HarnessEventIntent(
            _ids.NewEventId(),
            reservation.Granted
                ? HarnessEventKind.ModelStepCompleted
                : HarnessEventKind.ContinueRequested,
            modelStepId: modelStepId,
            payloadId: assistantPayloadId,
            safeCount: step.ToolCalls.Count,
            safeCode: reservation.ErrorCode);
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var stepEvents = new List<HarnessEventIntent> { promptComposedEvent, stepCompletedEvent };
        var stepPayloads = new List<HarnessPayloadIntent> { promptReceiptPayload, modelPayload };
        if (reservation.Granted) AddProviderContinuation(stepEvents, stepPayloads, modelStepId, providerContinuation);
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            stepEvents.Add(reasoningEvent);
            stepPayloads.Add(reasoningPayload);
        }
        var reservationNext = run.Advance(
            reservation.NextRun.Status,
            reservation.NextRun.BudgetWindow,
            reservation.NextRun.ModelStepsInWindow,
            reservation.NextRun.ToolCallsInWindow,
            appendedEventCount: stepEvents.Count,
            updatedAt: _time.UtcNow,
            terminalErrorCode: reservation.NextRun.TerminalErrorCode);
        run = await CommitAsync(
                run,
                reservationNext,
                stepEvents,
                stepPayloads,
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
        ModelContextSnapshot context,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText = null,
        ProviderContinuation? providerContinuation = null)
    {
        if (string.IsNullOrWhiteSpace(step.Text))
        {
            return await HandleModelReplyFailureAsync(
                    run,
                    modelStepId,
                    step,
                    droppedCount,
                    context,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken,
                    reasoningText)
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
        var continuationEvents = new List<HarnessEventIntent>();
        AddProviderContinuation(continuationEvents, payloads, modelStepId, providerContinuation);
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
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            payloads.Add(reasoningPayload);
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
        events.AddRange(continuationEvents);
        if (usagePayloadId is { } usageId)
        {
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ModelUsageRecorded,
                modelStepId: modelStepId,
                payloadId: usageId,
                safeCode: "reported"));
        }
        if (reasoningEvent is not null)
        {
            events.Add(reasoningEvent);
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

    /// <summary>
    /// Builds the durable reasoning record for a model step. Reasoning is
    /// observational only: it rides a client-visible payload the model context
    /// never binds, so reopening a session can show what the model was thinking
    /// without feeding it back into a later prompt.
    /// </summary>
    private (HarnessPayloadIntent? Payload, HarnessEventIntent? Event) BuildReasoningCommit(
        ModelStepId modelStepId,
        string? reasoningText)
    {
        var excerpt = ClipReasoning(reasoningText);
        if (excerpt is null)
        {
            return (null, null);
        }

        var bytes = Encoding.UTF8.GetBytes(excerpt);
        var payloadId = _ids.NewPayloadId();
        var payload = new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.ModelReasoning,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(bytes.Length, 1),
            bytes);
        var evt = new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.ModelReasoningRecorded,
            modelStepId: modelStepId,
            payloadId: payloadId,
            safeCode: "recorded");
        return (payload, evt);
    }

    private void AddProviderContinuation(
        List<HarnessEventIntent> events,
        List<HarnessPayloadIntent> payloads,
        ModelStepId modelStepId,
        ProviderContinuation? continuation)
    {
        if (continuation is null) return;
        var envelope = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            model_step_id = modelStepId.Value,
            continuation.Provider,
            continuation.Protocol,
            continuation.Model,
            continuation.Version,
            output_items = continuation.OutputItems,
        });
        if (envelope.Length > ProviderContinuation.MaximumEnvelopeBytes)
            throw new InvalidOperationException("provider_continuation_too_large");
        var payloadId = _ids.NewPayloadId();
        payloads.Add(new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.ProviderContinuation,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            ProviderContinuation.MaximumEnvelopeBytes,
            envelope));
        events.Add(new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.ProviderContinuationRecorded,
            modelStepId: modelStepId,
            payloadId: payloadId,
            safeCode: continuation.Protocol));
    }

    private static string? ClipReasoning(string? reasoningText)
    {
        if (string.IsNullOrWhiteSpace(reasoningText))
        {
            return null;
        }

        const int maxChars = 8_000;
        var trimmed = reasoningText.Trim();
        if (trimmed.Length > maxChars)
        {
            trimmed = trimmed[^maxChars..];
        }

        return trimmed.Length == 0 ? null : trimmed;
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
