using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Diagnostics;
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
        using var telemetryActivity = HarnessTelemetry.StartModelStepActivity();
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
            HarnessTelemetry.SetStepOutcome(telemetryActivity, "cancelled");
            throw;
        }
        catch (Exception error)
        {
            // The provider could not build the prompt. This one really is a prompt failure,
            // so it keeps the prompt code; only the model step's own failures below are
            // classified differently.
            RecordContractFailure(
                "context-provider",
                $"{error.GetType().Name}: {error.Message}",
                context.Entries.Count,
                capabilities: 0);
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt, telemetryActivity)
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
            HarnessTelemetry.SetStepOutcome(telemetryActivity, "cancelled");
            throw;
        }
        catch (Exception error)
        {
            // The runner threw, so the prompt is not what failed and saying it did points the
            // reader at the wrong subsystem. The code is classified from the exception rather
            // than forwarded, because a message can quote user content.
            string code = ModelStepFailureCode(error);
            if (ProviderTransientPauseException.IsProviderTransientCode(code))
            {
                // The provider kept failing transiently after the runner's bounded
                // retries. Failing the run here would burn it on a condition that
                // may clear while the user waits, so the coordinator parks the run
                // for an explicit Continue instead; no failure marking happens here.
                // The attempts before the throw may have streamed partial text, so
                // reset the stream before it completes: the Continue retry must
                // start on a clean turn instead of appending to the superseded text.
                assistantStream.Reset();
                assistantStream.Complete();
                throw new ProviderTransientPauseException(code, error);
            }

            if (AgentRecoveryNotes.IsModelRepairableCode(code))
            {
                // The model can fix this by taking another turn, so the coordinator
                // answers with a bounded recovery note instead of failing the run.
                // Failing here would burn the run before the model gets its chance.
                // The attempts before the throw may have streamed partial text, so
                // reset the stream before it completes: the retry must start on a
                // clean turn instead of appending to the superseded text.
                assistantStream.Reset();
                assistantStream.Complete();
                throw new ModelRepairableStepException(code, error);
            }

            assistantStream.Complete();

            RecordContractFailure(
                "model-step",
                $"{error.GetType().Name}: {error.Message}",
                context.Entries.Count,
                capabilities: 0);
            await MarkModelStepFailureAsync(run, modelStepId, thinkingStartedAt, telemetryActivity, code)
                .ConfigureAwait(false);
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
            HarnessTelemetry.SetStepOutcome(telemetryActivity, "cancelled");
            throw;
        }
        catch (ArgumentException error)
        {
            assistantStream.Complete();
            // Two different failures land here -- a receipt that does not match the prompt,
            // and a receipt too large to store -- and both used to surface as the same
            // opaque code with the reason discarded. The text is safe: it names a rule, not
            // any user content.
            RecordContractFailure(
                "validate",
                error.Message,
                promptContext.InstructionBlocks.Count,
                promptContext.Capabilities.Count);
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt, telemetryActivity)
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
        catch (Exception error)
        {
            assistantStream.Complete();
            RecordContractFailure(
                "serialize",
                error.Message,
                promptContext.InstructionBlocks.Count,
                promptContext.Capabilities.Count);
            await MarkPromptContractFailureAsync(run, modelStepId, thinkingStartedAt, telemetryActivity)
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
                    safeCode: "succeeded",
                    detail: RedactDetail(modelResult.ReasoningText)));
        HarnessTelemetry.SetStepOutcome(telemetryActivity, "succeeded");
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
            modelDroppedCount,
            modelResult.ReasoningText,
            modelResult.ProviderContinuation);
    }

    /// <summary>
    /// Records why a prompt receipt was rejected, when diagnostics are asked for.
    /// </summary>
    /// <remarks>
    /// The run reports one code for every rejection, which says a receipt was refused but
    /// not which rule it broke, and the two rules here have nothing in common: a receipt can
    /// disagree with the prompt, or be too large to store. Off unless RU_PROMPT_DIAGNOSTIC
    /// names a file, because this writes on a failure path normal runs never take.
    /// </remarks>
    private static void RecordContractFailure(string stage, string reason, int instructionBlocks, int capabilities)
    {
        string? target = Environment.GetEnvironmentVariable("RU_PROMPT_DIAGNOSTIC");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                target,
                $"{DateTime.UtcNow:O} {stage}: {reason} | instructionBlocks={instructionBlocks}" +
                $" capabilities={capabilities}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The code that describes why the model step itself failed.
    /// </summary>
    /// <remarks>
    /// Every failure here used to be reported as prompt_contract_invalid, which named the
    /// wrong subsystem: the prompt was fine, the runner was not, and the reader got a code
    /// that pointed them at the prompt and no reason for anything. A prompt too large to
    /// render and a provider that refused the call are different problems with different
    /// fixes, so they get different codes. The message is classified, never forwarded: it can
    /// quote user content, and these codes cross into the transcript.
    /// </remarks>
    private static string ModelStepFailureCode(Exception error) => error switch
    {
        ArgumentException => "prompt_too_large",
        TimeoutException => "model_timeout",
        OperationCanceledException => "cancelled",
        IStableStepError stable when IsStableStepCode(stable.StepErrorCode) => stable.StepErrorCode.Trim(),
        _ => "model_failed",
    };

    /// <summary>Terminal-code shape: 1..64 chars, [a-z] first, [a-z0-9_.] after.</summary>
    private static bool IsStableStepCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        code = code.Trim();
        if (code.Length is < 1 or > 64 || code[0] < 'a' || code[0] > 'z')
        {
            return false;
        }

        foreach (var ch in code)
        {
            if ((ch < 'a' || ch > 'z') && (ch < '0' || ch > '9') && ch != '.' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private async Task MarkModelStepFailureAsync(
        AgentRun run,
        ModelStepId modelStepId,
        DateTimeOffset startedAt,
        System.Diagnostics.Activity? telemetryActivity,
        string safeCode)
    {
        HarnessTelemetry.SetStepOutcome(telemetryActivity, safeCode);
        await _lifecycle.FailAsync(run.RunId, safeCode, CancellationToken.None).ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectModelStep(
                modelStepId,
                AgentActivityState.Failed,
                ElapsedMilliseconds(startedAt),
                safeCode: safeCode));
    }

    /// <summary>
    /// Marks a step failure whose recovery was refused (repair notes spent, or the
    /// run changed state and could not park), preserving the pre-recovery terminal
    /// path: fail the run with the original code and publish the failed model-step
    /// activity.
    /// </summary>
    internal async Task FailStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        string safeCode)
    {
        await MarkModelStepFailureAsync(
                run,
                modelStepId,
                _time.UtcNow,
                telemetryActivity: null,
                safeCode)
            .ConfigureAwait(false);
    }

    private async Task MarkPromptContractFailureAsync(
        AgentRun run,
        ModelStepId modelStepId,
        DateTimeOffset startedAt,
        System.Diagnostics.Activity? telemetryActivity)
    {
        HarnessTelemetry.SetStepOutcome(telemetryActivity, "prompt_contract_invalid");
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

    private string? RedactDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        // Reasoning can echo context secrets; run it through the same
        // redactor as streamed deltas before it crosses the sink boundary.
        var session = _transientRedactor.CreateSession();
        return session.Append(detail) + session.Complete();
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

        if (delta.IsReset)
        {
            stream.Reset();
            return ValueTask.CompletedTask;
        }

        stream.Append(delta.Content);
        return ValueTask.CompletedTask;
    }

    internal sealed record ModelInvocationResult(
        AssistantStep Step,
        HarnessPayloadIntent PromptReceiptPayload,
        HarnessEventIntent PromptComposedEvent,
        int ModelDroppedCount,
        string? ReasoningText = null,
        ProviderContinuation? ProviderContinuation = null);
}
