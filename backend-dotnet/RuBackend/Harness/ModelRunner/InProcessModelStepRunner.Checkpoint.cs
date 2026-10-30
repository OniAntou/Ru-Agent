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
                context, request.PromptContext, request.Preset, request.PolicySnapshotVersion, request.PolicySnapshotSha256),
                null, timeout.Token, preparingSnapshot: true).ConfigureAwait(false);
            var bootstrap = source.ProviderBootstrap.WithReducedEffort();
            var cap = Math.Min(4096, bootstrap.MaximumOutputTokens ?? 4096);
            var summaryBootstrap = new ProviderBootstrapData(bootstrap.Backend, bootstrap.Protocol,
                bootstrap.Endpoint, bootstrap.Model, bootstrap.Credential, bootstrap.RequestOptions, cap,
                bootstrap.ToolCallingMode, allowNativeResponses: true, allowNativeAnthropic: true,
                bootstrap.UseMaxCompletionTokens, bootstrap.ContextWindowTokens, cap,
                replayChatReasoning: bootstrap.ReplayChatReasoning,
                acceptsImageInput: bootstrap.AcceptsImageInput);
            // Reuse protocol-specific continuations, including opaque provider reasoning, without a dispatch loop.
            var generation = new ModelGenerationRequest(source.Prompt, source.ModelMessages, source.SystemPromptContext,
                source.Settings, summaryBootstrap, source.Images, source.ResponsesInputItems, source.AnthropicMessages,
                source.PromptInHistory, source.PendingRuntimeSnapshot);
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
            : ProviderTransientPauseException.RequiresExplicitContinue(error.Code)
            ? "checkpoint_provider_transient" : "checkpoint_provider_failed", error); }
        catch (ModelRunnerProtocolException error)
        { throw new ContextCheckpointException("checkpoint_input_invalid", error); }
    }
}
