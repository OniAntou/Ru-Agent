using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
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
            // a model may ignore the reduced-effort request, so the summary needs real
            // headroom: a verbose completion cut off at 4,096 tokens ended the run as
            // checkpoint_incomplete. The cap is now up to 32,768 (the order of magnitude
            // other agents allow for a summarizer), but never above the run's pinned output
            // reserve or a quarter of the model's window, so requesting the summary can
            // never exceed what the provider is pinned to serve or squeeze its own input.
            var cap = CheckpointOutputCap(bootstrap);
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

    /// <summary>
    /// Output tokens one checkpoint (summary) call may request: up to 32,768, but no more
    /// than the run's pinned output reserve or a quarter of the model's window. The quarter
    /// keeps three quarters of the window for the conversation the summary replays; the pin
    /// keeps the auxiliary call inside what the provider was already pinned to serve.
    /// </summary>
    internal static int CheckpointOutputCap(ProviderBootstrapData bootstrap)
    {
        var windowTokens = bootstrap.ContextWindowTokens ?? ContextWindowDefaults.UnknownContextWindowTokens;
        var quarterOfWindow = Math.Max(1_024L, windowTokens / 4);
        return (int)Math.Min(32_768L, Math.Min((long)bootstrap.OutputTokenReserve, quarterOfWindow));
    }
}
