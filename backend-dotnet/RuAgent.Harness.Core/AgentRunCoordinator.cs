using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core;

public sealed class AgentRunCoordinator
{
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly AgentContextAssembler _contextAssembler;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly TransientFrameBuffer _frames;
    private readonly AgentToolExecutionPipeline _tools;

    public AgentRunCoordinator(
        IModelStepRunner model,
        IToolGateway gateway,
        IHarnessEventStore store,
        AgentContextAssembler contextAssembler,
        IIdGenerator ids,
        ITimeProvider time,
        ToolCallScheduler scheduler,
        TransientFrameBuffer frames)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextAssembler = contextAssembler
            ?? throw new ArgumentNullException(nameof(contextAssembler));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _tools = new AgentToolExecutionPipeline(
            gateway ?? throw new ArgumentNullException(nameof(gateway)),
            store,
            ids,
            time,
            scheduler ?? throw new ArgumentNullException(nameof(scheduler)),
            frames);
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

        var request = new ModelStepRequest(
            run.RunId,
            modelStepId,
            context,
            run.Preset,
            run.PolicySnapshotVersion,
            run.PolicySnapshotSha256);
        var droppedBefore = _frames.DroppedCount;
        var step = await _model.RunStepAsync(
                request,
                delta => PublishAssistantDelta(run.RunId, modelStepId, delta),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("model_step_returned_null");
        var modelDroppedCount = checked((int)(_frames.DroppedCount - droppedBefore));

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
        var modelPayloads = new List<HarnessPayloadIntent>();
        PayloadId? assistantPayloadId = null;
        if (!string.IsNullOrEmpty(step.Text))
        {
            var assistantBytes = Encoding.UTF8.GetBytes(step.Text);
            assistantPayloadId = _ids.NewPayloadId();
            modelPayloads.Add(new HarnessPayloadIntent(
                assistantPayloadId.Value,
                HarnessPayloadKind.AssistantText,
                HarnessPayloadAudience.AuthenticatedClient
                    | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text,
                Math.Max(assistantBytes.Length, 1),
                assistantBytes));
        }

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
                modelPayloads,
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

    private ValueTask PublishAssistantDelta(
        RunId runId,
        ModelStepId expectedStepId,
        AssistantDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.ModelStepId != expectedStepId)
        {
            throw new InvalidOperationException("model_delta_identity_mismatch");
        }

        _frames.TryPublish(new HarnessTransientFrame(
            runId,
            TransientFrameKind.AssistantDelta,
            delta.Sequence,
            delta.Content,
            modelStepId: expectedStepId));
        return ValueTask.CompletedTask;
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
