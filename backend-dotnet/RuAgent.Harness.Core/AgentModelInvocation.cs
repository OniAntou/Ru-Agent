using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

/// <summary>
/// Executes the model-invocation phase of a run step: prompt-context build, model
/// call with streaming deltas, receipt validation and the composed-receipt payload.
/// Moved verbatim from AgentRunCoordinator.RunOneStepAsync; all failure paths
/// (cancelled, prompt_contract_invalid, model_failed) preserve exact semantics.
/// </summary>
internal sealed class AgentModelInvocation
{
    private readonly IModelStepRunner _model;
    private readonly ISystemPromptContextProvider _promptContextProvider;
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactor _transientRedactor;
    private readonly IAgentActivitySink? _activitySink;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentRunLifecycle _lifecycle;

    public AgentModelInvocation(
        IModelStepRunner model,
        ISystemPromptContextProvider promptContextProvider,
        TransientFrameBuffer frames,
        ITransientContentRedactor transientRedactor,
        IAgentActivitySink? activitySink,
        IIdGenerator ids,
        ITimeProvider time,
        AgentRunLifecycle lifecycle)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _promptContextProvider = promptContextProvider
            ?? throw new ArgumentNullException(nameof(promptContextProvider));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _transientRedactor = transientRedactor
            ?? throw new ArgumentNullException(nameof(transientRedactor));
        _activitySink = activitySink;
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<ModelInvocationResult> InvokeAsync(
        AgentRun run,
        ModelContextSnapshot context,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
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

        return new ModelInvocationResult(
            step,
            promptReceiptPayload,
            promptComposedEvent,
            modelDroppedCount);
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

    internal sealed record ModelInvocationResult(
        AssistantStep Step,
        HarnessPayloadIntent PromptReceiptPayload,
        HarnessEventIntent PromptComposedEvent,
        int ModelDroppedCount);
}
