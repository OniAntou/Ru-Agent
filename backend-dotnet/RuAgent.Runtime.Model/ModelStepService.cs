using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Diagnostics;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

public sealed partial class ModelStepService
{
    /// <summary>
    /// Environment variable that lowers the provider-step bound for a local
    /// single-slot runtime. Absent, blank, non-numeric or below one falls back
    /// to <see cref="DefaultMaxConcurrentProviderSteps"/>.
    /// </summary>
    public const string MaxConcurrentProviderStepsVariable = "RU_MAX_CONCURRENT_MODEL_STEPS";

    /// <summary>
    /// Matches the runtime loop's own run concurrency, so a burst of runs in
    /// different workspaces can all reach the model instead of queueing behind
    /// one serialized generation. Lower it to 1 for llama.cpp / vLLM / Ollama.
    /// </summary>
    public const int DefaultMaxConcurrentProviderSteps = 4;

    /// <summary>Tail-kept provider reasoning per step. The projector clamps
    /// further for the activity wire; this cap bounds in-process memory.</summary>
    private const int MaximumReasoningChars = 8_000;
    private const int ImageInputTokenAllowance = RequestContextEstimator.ImageInputTokenAllowance;
    private const long UnknownContextWindowTokens = 32_768;
    // Legacy literal: the direct (non-provider) step id is folded into the
    // synthetic provider call id as "ru_call_<step>_1", and that id is durable —
    // it lands in agent_events with the tool proposal. Renaming it would orphan
    // every stored proposal, so the value stays even though the name of the
    // surrounding module no longer says "worker".
    private const string DirectModelStepId = "worker";
    private readonly IModelProviderResolver _providers;
    private readonly SemaphoreSlim _providerGate;
    private readonly RequestTokenAnchor _tokenAnchors = new();

    /// <summary>
    /// Bounds how many provider generations may be in flight at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists for the server, not for this class: a local single-slot runtime
    /// (llama.cpp, vLLM, Ollama) accepts one request at a time and rejects the rest,
    /// so the bound must be lowerable to 1. It does not protect shared state — every
    /// field here is either immutable after construction or local to one call, and
    /// the provider adapters hold only an injected thread-safe <c>HttpClient</c>.
    /// </para>
    /// <para>
    /// The runtime loop already drives up to four runs concurrently. With a bound of
    /// one, that concurrency was decorative for model steps: every other run blocked
    /// on this gate, and a generation that hung held the gate for as long as the run
    /// lived. The bound is therefore configurable and defaults to the runtime's own
    /// run concurrency.
    /// </para>
    /// </remarks>
    public ModelStepService(
        IModelProviderResolver providers,
        int maxConcurrentProviderSteps = DefaultMaxConcurrentProviderSteps)
        : this(null, providers, maxConcurrentProviderSteps) { }

    public ModelStepService(
        IModelProvider provider,
        int maxConcurrentProviderSteps = DefaultMaxConcurrentProviderSteps)
        : this(provider, null, maxConcurrentProviderSteps) { }

    private ModelStepService(
        IModelProvider? singleProvider,
        IModelProviderResolver? providers,
        int maxConcurrentProviderSteps)
    {
        if (maxConcurrentProviderSteps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentProviderSteps),
                "At least one provider step must be allowed to run.");
        }

        _providers = providers ?? (singleProvider is not null ? new ModelProviderResolver(new Dictionary<string, IModelProvider>
        {
            ["chat_completions"] = singleProvider,
            ["openai_responses"] = singleProvider,
            ["anthropic_messages"] = singleProvider,
        }) : throw new ArgumentNullException(nameof(providers)));
        _providerGate = new SemaphoreSlim(maxConcurrentProviderSteps, maxConcurrentProviderSteps);
    }

    /// <summary>
    /// Reads <c>RU_MAX_CONCURRENT_MODEL_STEPS</c>, falling back to
    /// <see cref="DefaultMaxConcurrentProviderSteps"/> when it is absent, blank,
    /// not an integer, or below one.
    /// </summary>
    public static int ResolveMaxConcurrentProviderSteps(string? configured) =>
        int.TryParse(configured, out var parsed) && parsed >= 1
            ? parsed
            : DefaultMaxConcurrentProviderSteps;

    public async Task<ModelStepServiceResult> RunAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        => await RunAsync(request, null, cancellationToken).ConfigureAwait(false);

    public async Task<ModelStepServiceResult> RunAsync(
        ModelGenerationRequest request,
        Func<string, ValueTask>? onDelta,
        CancellationToken cancellationToken,
        string? modelStepId = null)
    {
        var prepared = PrepareRequest(request, modelStepId, requireExactProjection: false);
        return await RunPreparedAsync(prepared, onDelta, cancellationToken, modelStepId).ConfigureAwait(false);
    }

    public PreparedModelStep PrepareRequest(
        ModelGenerationRequest request,
        string? modelStepId = null,
        bool requireExactProjection = true,
        string? contextScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providerBootstrap = request.ProviderBootstrap;
        // The pinned bootstrap mode is the declared provider capability; each
        // protocol adapter projects its own native tool-call contract.
        var nativeToolCalling = providerBootstrap.ToolCallingMode == ProviderToolCallingMode.Native;
        // The receipt returned with this step must bind the text it was actually
        // composed from, not just the context shape. The text exists only here,
        // so the rendered-prompt hash is verified before the step is dispatched.
        // Composing and building the request is part of interpreting the Host's
        // input: a malformed history or an oversized prompt is deterministic, so
        // it must fail as a non-retryable input error instead of escaping to the
        // generic generation_failed mapping (which would spend the retry budget).
        SystemPromptSnapshot snapshot;
        ModelRequest modelRequest;
        JsonObject settings;
        try
        {
            var nativeResultIds = request.ModelMessages.Where(turn => turn.Role == "tool" && turn.ToolCallId is not null)
                .Select(turn => turn.ToolCallId!).ToHashSet(StringComparer.Ordinal);
            snapshot = SystemPromptComposer.Compose(request.SystemPromptContext, nativeToolCalling, nativeResultIds,
                deferRuntimeContext: nativeToolCalling, recipe: SelectPromptRecipe(providerBootstrap, nativeToolCalling));
            snapshot.Receipt.ValidateAgainst(request.SystemPromptContext, snapshot.Text);
            settings = BuildWireSettings(request.Settings);
            var requestTurns = nativeToolCalling
                ? request.ModelMessages
                : ProjectTextTurns(request.ModelMessages, settings);
            modelRequest = new ModelRequest(
                providerBootstrap.Model,
                snapshot.Text,
                request.Prompt,
                requestTurns,
                BuildRequestSettings(settings, providerBootstrap.RequestOptions),
                providerBootstrap.Endpoint,
                providerBootstrap.Credential,
                providerBootstrap.MaximumOutputTokens,
                request.Images,
                request.ResponsesInputItems,
                nativeToolCalling,
                request.AnthropicMessages,
                providerBootstrap.UseMaxCompletionTokens,
                runtimeContext: request.PromptInHistory ? null : snapshot.RuntimeContext,
                promptInHistory: request.PromptInHistory,
                replayChatReasoning: nativeToolCalling && providerBootstrap.ReplayChatReasoning);
        }
        catch (ModelStepException)
        {
            throw;
        }
        catch (AgentProtocolException error)
        {
            // The retired worker server translated an AgentProtocolException from
            // step assembly into a protocol_failure error frame, and the input is
            // still deterministic (an assistant turn naming a tool the settings
            // never advertised, or an unprojectable policy decision), so it keeps
            // that code here instead of escaping to the generic mapping.
            throw new ModelStepException("protocol_failure", retryable: false, error);
        }
        catch (ArgumentException error) when (error.ParamName == "turns")
        {
            throw new ModelStepException("model_history_invalid", retryable: false, error);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new ModelStepException("invalid_request", retryable: false, error);
        }

        IModelProvider provider;
        try { provider = _providers.Resolve(providerBootstrap.Protocol); }
        catch (ArgumentException error) { throw new ModelStepException("provider_bootstrap_invalid", false, error); }
        var estimatedInputTokens = -1L;
        var maxPromptTokens = 0L;
        if (provider is IModelRequestBodyProjector projector)
        {
            var body = projector.ProjectRequestBody(modelRequest);
            modelRequest = modelRequest.WithPreparedBodyJson(body);
            (estimatedInputTokens, maxPromptTokens) = MeasurePreparedRequest(modelRequest, providerBootstrap);
            if (contextScope is not null && !modelRequest.AllImages.Any() && request.PromptInHistory)
                estimatedInputTokens = _tokenAnchors.Estimate(contextScope, body,
                    providerBootstrap.Protocol + "|" + providerBootstrap.Endpoint.AbsoluteUri, estimatedInputTokens);
        }
        else if (requireExactProjection)
        {
            throw new ModelStepException("provider_request_projection_unavailable", retryable: false);
        }

        return new PreparedModelStep(
            request, modelRequest, snapshot, settings, provider, estimatedInputTokens, maxPromptTokens, contextScope);
    }

    public async Task<ModelStepServiceResult> RunPreparedAsync(
        PreparedModelStep prepared,
        Func<string, ValueTask>? onDelta,
        CancellationToken cancellationToken,
        string? modelStepId = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        var request = prepared.Source;
        var snapshot = prepared.Snapshot;
        var modelRequest = prepared.Request;
        var settings = prepared.Settings;
        var providerBootstrap = request.ProviderBootstrap;
        var provider = prepared.Provider;
        var holdback = new DeltaHoldback(onDelta);

        var providerGateEntered = false;
        try
        {
            using (HarnessPerformance.Measure(HarnessPhase.ProviderGate))
                await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            providerGateEntered = true;
            var text = new StringBuilder();
            var reasoning = new StringBuilder();
            var replayReasoning = modelRequest.ReplayChatReasoning ? new StringBuilder() : null;
            var replayReasoningBytes = 0;
            var toolFragments = new List<ModelToolFragment>();
            ProviderContinuation? providerContinuation = null;
            ResponsesAssistantMessages? responsesMessages = null;
            string? finishReason = null;
            ModelUsage? usage = null;
            try
            {
                // A completed delta ends the *content* of the step, not the
                // stream: include_usage sends its tail chunk after finish_reason,
                // so the enumeration keeps draining until the provider closes,
                // and only usage information still reaches the step.
                using var timing = HarnessPerformance.Measure(HarnessPhase.ProviderStream);
                var providerStarted = Stopwatch.GetTimestamp();
                bool firstToken = false, firstText = false, firstReasoning = false, firstTool = false;
                var stepCompleted = false;
                await foreach (var delta in provider.GenerateAsync(modelRequest, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stepCompleted)
                    {
                        if (delta.Usage is not null)
                        {
                            usage = delta.Usage;
                        }

                        continue;
                    }

                    var hasText = delta.Text is { Length: > 0 };
                    var hasReasoning = delta.Reasoning is { Length: > 0 };
                    var hasTool = delta.ToolFragments.Any(fragment => fragment.ToolName.Length > 0 || fragment.ArgumentsFragment.Length > 0);
                    if (!firstToken && (hasText || hasReasoning || hasTool))
                    { HarnessPerformance.RecordElapsed(HarnessPhase.FirstToken, providerStarted); firstToken = true; }
                    if (!firstText && hasText)
                    { HarnessPerformance.RecordElapsed(HarnessPhase.FirstText, providerStarted); firstText = true; }
                    if (!firstReasoning && hasReasoning)
                    { HarnessPerformance.RecordElapsed(HarnessPhase.FirstReasoning, providerStarted); firstReasoning = true; }
                    if (!firstTool && hasTool)
                    { HarnessPerformance.RecordElapsed(HarnessPhase.FirstTool, providerStarted); firstTool = true; }

                    if (delta.Text is not null)
                    {
                        text.Append(delta.Text);
                        if (text.Length > ModelGenerationBounds.MaxTextChars)
                        {
                            throw new ModelStepException("protocol_failure", retryable: false);
                        }

                        await holdback.ForwardAsync(delta.Text).ConfigureAwait(false);
                    }

                    if (delta.ResponsesMessages is { } messages)
                    {
                        if (responsesMessages is not null || !delta.Completed
                            || providerBootstrap.Protocol != ProviderProtocols.OpenAiResponses
                            || providerBootstrap.ToolCallingMode != ProviderToolCallingMode.Native)
                            throw new ModelStepException("protocol_failure", false);
                        responsesMessages = messages;
                    }
                    toolFragments.AddRange(delta.ToolFragments);
                    if (delta.ProviderContinuation is { } continuation)
                    {
                        if (providerContinuation is not null
                            || providerBootstrap.ToolCallingMode != ProviderToolCallingMode.Native
                            || providerBootstrap.Protocol is not (ProviderProtocols.OpenAiResponses or ProviderProtocols.AnthropicMessages)
                            || continuation.Model != providerBootstrap.Model)
                            throw new ModelStepException("protocol_failure", retryable: false);
                        providerContinuation = continuation;
                    }
                    // Long argument streams arrive as one fragment per SSE chunk;
                    // the per-chunk shape limit lives in the adapter. This cap only
                    // bounds memory, and the byte cap on reassembled arguments
                    // (model_tool_call_invalid) is what bounds the call itself.
                    if (toolFragments.Count > 2048)
                    {
                        throw new ModelStepException("protocol_failure", retryable: false);
                    }

                    if (delta.Reasoning is not null)
                    {
                        if (replayReasoning is not null)
                        {
                            replayReasoningBytes = checked(replayReasoningBytes + Encoding.UTF8.GetByteCount(delta.Reasoning));
                            if (replayReasoningBytes > ProviderContinuation.MaximumBytes)
                                throw new ModelStepException("provider_continuation_too_large", false);
                            replayReasoning.Append(delta.Reasoning);
                        }
                        reasoning.Append(delta.Reasoning);
                        if (reasoning.Length > MaximumReasoningChars)
                        {
                            reasoning.Remove(0, reasoning.Length - MaximumReasoningChars);
                        }
                    }

                    if (delta.FinishReason is not null)
                    {
                        finishReason = delta.FinishReason;
                    }

                    if (delta.Usage is not null)
                    {
                        usage = delta.Usage;
                    }

                    if (delta.Completed)
                    {
                        stepCompleted = true;
                    }
                }
            }
            catch (ModelProviderException mpe)
            {
                throw MapProviderException(mpe);
            }

        finishReason ??= toolFragments.Count > 0 ? "tool_calls" : "stop";
        if (finishReason is "length" or "content_filter")
        {
            // A completed JSON call can precede a cut-off or filtered call in
            // the same batch. Preserve the provider's finish marker before
            // parsing or promoting any calls, so no partial batch can execute.
            return new ModelStepServiceResult(text.Length == 0 ? null : text.ToString(),
                [], finishReason, snapshot.Receipt, usage, ExtractReasoningText(reasoning, text));
        }
        var providerStepId = string.IsNullOrWhiteSpace(modelStepId)
            ? DirectModelStepId
            : modelStepId;
        var (calls, visibleText) = ParseToolCalls(toolFragments, settings, finishReason, text, providerStepId);
        if (calls.Count > 0)
        {
            finishReason = "tool_calls";
        }
        if (replayReasoning is not null && calls.Count > 0)
        {
            if (providerContinuation is not null) throw new ModelStepException("protocol_failure", false);
            try
            {
                providerContinuation = ChatProviderContinuation.Create(providerBootstrap.Model, visibleText,
                    replayReasoning.ToString(), calls);
            }
            catch (ArgumentException error)
            {
                throw new ModelStepException("provider_continuation_too_large", false, error);
            }
        }
        if (responsesMessages is not null && responsesMessages.Text != (visibleText ?? string.Empty))
            throw new ModelStepException("protocol_failure", false);
        ValidateProviderContinuation(providerBootstrap, providerContinuation, calls);
        if (calls.Count == 0)
        {
            await holdback.ReleaseAsync().ConfigureAwait(false);
        }

        if (providerBootstrap.ToolCallingMode == ProviderToolCallingMode.Native
            && providerBootstrap.Protocol is ProviderProtocols.OpenAiResponses or ProviderProtocols.AnthropicMessages
            && calls.Count > 0 && providerContinuation is null)
            throw new ModelStepException("provider_continuation_missing", retryable: false);
        if (prepared.ContextScope is { } scope && prepared.Request.PreparedBodyJson is { } body
            && prepared.Source.Images.Count == 0 && prepared.Source.PromptInHistory)
            _tokenAnchors.Observe(scope, body, providerBootstrap.Protocol + "|" + providerBootstrap.Endpoint.AbsoluteUri,
                providerBootstrap.Model, usage);
        if (usage is not null) usage = usage with { ContextTokens = prepared.ContextTokens };
        return new ModelStepServiceResult(visibleText, calls, finishReason, snapshot.Receipt, usage, ExtractReasoningText(reasoning, text), providerContinuation, responsesMessages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModelStepException)
        {
            throw;
        }
        catch (ModelProviderException error)
        {
            throw MapProviderException(error);
        }
        catch (Exception error)
        {
            throw new ModelStepException("generation_failed", retryable: true, error);
        }
        finally
        {
            if (providerGateEntered)
            {
                _providerGate.Release();
            }
        }
    }

    private static (long EstimatedInputTokens, long MaxPromptTokens) MeasurePreparedRequest(
        ModelRequest request,
        ProviderBootstrapData bootstrap)
    {
        var body = request.PreparedBodyJson
            ?? throw new ModelStepException("provider_request_projection_unavailable", false);
        var wireBytes = Encoding.UTF8.GetByteCount(body);
        if (wireBytes > ModelRequest.MaximumSerializedBodyBytes)
            throw new ModelStepException("request_too_large", retryable: false);
        var imageBytes = request.AllImages.Sum(image => (long)Encoding.UTF8.GetByteCount(image.Base64Data));
        var textBytes = Math.Max(0, wireBytes - imageBytes);
        var imageAllowance = checked((long)request.AllImages.Count() * ImageInputTokenAllowance);
        var inputTokens = checked((textBytes + 3) / 4 + imageAllowance);
        var windowTokens = bootstrap.ContextWindowTokens ?? UnknownContextWindowTokens;
        var maxPromptTokens = Math.Max(1, windowTokens - bootstrap.OutputTokenReserve);
        return (inputTokens, maxPromptTokens);
    }

    private static PromptRecipe SelectPromptRecipe(ProviderBootstrapData bootstrap, bool nativeToolCalling) =>
        nativeToolCalling && bootstrap.Protocol == ProviderProtocols.OpenAiResponses
        && bootstrap.Endpoint.Scheme == "https" && bootstrap.Endpoint.Port == 443
        && string.Equals(bootstrap.Endpoint.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
        && bootstrap.Model == "gpt-5.3-codex"
            ? PromptRecipe.CodexNative : PromptRecipe.Neutral;

    private static void ValidateProviderContinuation(
        ProviderBootstrapData bootstrap,
        ProviderContinuation? continuation,
        IReadOnlyList<ModelToolCall> calls)
    {
        if (bootstrap.ReplayChatReasoning && bootstrap.Protocol == ProviderProtocols.ChatCompletions)
        {
            if (calls.Count == 0 && continuation is null) return;
            if (continuation is null || continuation.Provider != "chat" || continuation.Protocol != bootstrap.Protocol
                || continuation.Model != bootstrap.Model || calls.Count == 0)
                throw new ModelStepException("provider_continuation_missing", false);
            return; // The bounded private message is formed from this step's validated calls above.
        }
        if (bootstrap.ToolCallingMode != ProviderToolCallingMode.Native
            || bootstrap.Protocol is not (ProviderProtocols.OpenAiResponses or ProviderProtocols.AnthropicMessages))
        {
            if (continuation is not null) throw new ModelStepException("protocol_failure", false);
            return;
        }
        if (calls.Count == 0)
        {
            if (continuation is not null) throw new ModelStepException("protocol_failure", false);
            return;
        }
        var expectedProvider = bootstrap.Protocol == ProviderProtocols.AnthropicMessages ? "anthropic" : "openai";
        if (continuation is null || continuation.Provider != expectedProvider || continuation.Protocol != bootstrap.Protocol || continuation.Model != bootstrap.Model)
            throw new ModelStepException("provider_continuation_missing", false);
        var nativeCalls = continuation.OutputItems
            .Where(item => item["type"]?.GetValue<string>() == (expectedProvider == "anthropic" ? "tool_use" : "function_call"))
            .ToArray();
        if (nativeCalls.Length != calls.Count) throw new ModelStepException("protocol_failure", false);
        for (var index = 0; index < calls.Count; index++)
        {
            var item = nativeCalls[index];
            var callId = expectedProvider == "anthropic" ? item["id"]?.GetValue<string>() : item["call_id"]?.GetValue<string>();
            var arguments = expectedProvider == "anthropic"
                ? item["input"]?.ToJsonString()
                : item["arguments"]?.GetValue<string>();
            if (callId != calls[index].ProviderCallId
                || item["name"]?.GetValue<string>() != calls[index].ToolId
                || arguments is null
                || !System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(arguments),
                    System.Text.Json.Nodes.JsonNode.Parse(calls[index].Arguments.GetRawText())))
                throw new ModelStepException("protocol_failure", false);
        }
    }
}
