using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Model;

namespace RuBackend.Harness.ModelRunner;

public sealed partial class InProcessModelStepRunner
{
    internal TimeSpan CheckpointTimeoutOverride { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<ContextCheckpointResult> SummarizeAsync(ModelStepRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckpointTimeoutOverride);
        try
        {
            var context = ContextCheckpoint.WithDirective(request.Context);
            var source = await BuildGenerationRequestAsync(new ModelStepRequest(request.RunId, request.ModelStepId,
                context, request.PromptContext, request.Preset, request.PolicySnapshotVersion, request.PolicySnapshotSha256,
                sessionId: request.SessionId),
                null, timeout.Token, preparingSnapshot: true).ConfigureAwait(false);
            var bootstrap = source.ProviderBootstrap.WithReducedEffort();
            // Reasoning tokens draw from the same completion budget on every protocol, and
            // this model may ignore the reduced-effort request. A 4,096 cap truncated the
            // checkpoint mid-summary on a verbose model (finish_reason=length), which ended
            // the run as checkpoint_incomplete; the summary is supposed to be at most 8,000
            // characters, so the cap only bounds a runaway completion. Raise it to the pinned
            // reserve when that is larger, still bounded by the released 8,192 default.
            var cap = Math.Min(8_192, bootstrap.MaximumOutputTokens ?? 8_192);
            var summaryBootstrap = new ProviderBootstrapData(bootstrap.Backend, bootstrap.Protocol,
                bootstrap.Endpoint, bootstrap.Model, bootstrap.Credential, bootstrap.RequestOptions, cap,
                useMaxCompletionTokens: bootstrap.UseMaxCompletionTokens,
                contextWindowTokens: bootstrap.ContextWindowTokens, outputTokenReserve: cap,
                replayChatReasoning: bootstrap.ReplayChatReasoning,
                acceptsImageInput: bootstrap.AcceptsImageInput,
                sessionHeaderName: bootstrap.SessionHeaderName);
            // Reuse protocol-specific continuations, including opaque provider reasoning, without a dispatch loop.
            var generation = new ModelGenerationRequest(source.Prompt, source.ModelMessages, source.SystemPromptContext,
                source.Settings, summaryBootstrap, source.Images, source.ResponsesInputItems, source.AnthropicMessages,
                source.PromptInHistory, source.PendingRuntimeSnapshot, source.SessionId);
            var prepared = _stepService.PrepareRequest(generation, request.ModelStepId.Value);
            if (prepared.EstimatedInputTokens > prepared.MaxPromptTokens - RequestSafetyMarginTokens)
                throw new ContextCheckpointException("checkpoint_input_too_large");
            var result = await _stepService.RunPreparedAsync(prepared, null, timeout.Token, request.ModelStepId.Value).ConfigureAwait(false);
            var code = result.ToolCalls.Count > 0 ? "checkpoint_tool_rejected"
                : result.FinishReason != "stop" ? "checkpoint_incomplete" : null;
            return new ContextCheckpointResult(result.Text ?? string.Empty, result.Usage, code, result.PromptReceipt);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new ContextCheckpointException(timeout.IsCancellationRequested ? "checkpoint_timeout" : "checkpoint_provider_transient", error); }
        catch (ModelStepException error)
        { throw new ContextCheckpointException(error.Code == "context_window_exceeded" ? "checkpoint_context_overflow"
            : ProviderTransientPauseException.EndsRunWithProviderCode(error.Code)
            ? "checkpoint_provider_transient" : "checkpoint_provider_failed", error); }
        catch (ModelRunnerProtocolException error)
        { throw new ContextCheckpointException("checkpoint_input_invalid", error); }
    }
}
