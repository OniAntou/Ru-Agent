using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Worker;

public sealed record WorkerV2StepResult(
    string? Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string FinishReason,
    SystemPromptReceipt PromptReceipt,
    ModelUsage? Usage,
    string? ReasoningText = null);

public sealed class WorkerV2StepException : Exception
{
    public WorkerV2StepException(string code, bool retryable, Exception? innerException = null)
        : base($"worker_v2_{code}", innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

public sealed class WorkerV2StepService
{
    /// <summary>Tail-kept provider reasoning per step. The projector clamps
    /// further for the activity wire; this cap bounds worker memory.</summary>
    private const int MaximumReasoningChars = 8_000;
    private const string DirectModelStepId = "worker";
    private static readonly WorkerV2Options DefaultOptions = new(
        0,
        "test-worker",
        new Uri("http://127.0.0.1"),
        "managed-agent",
        string.Empty);
    private readonly IModelProviderResolver _providers;
    private readonly IModelProvider? _singleProvider;
    private readonly WorkerV2Options _options;
    private readonly SemaphoreSlim _providerGate = new(1, 1);

    public WorkerV2StepService(
        IModelProviderResolver providers,
        WorkerV2Options? options = null)
        : this((IModelProvider?)null, providers, options) { }

    public WorkerV2StepService(
        IModelProvider provider,
        WorkerV2Options? options = null)
        : this(provider, null, options) { }

    private WorkerV2StepService(
        IModelProvider? singleProvider,
        IModelProviderResolver? providers,
        WorkerV2Options? options)
    {
        _singleProvider = singleProvider;
        _providers = providers ?? (singleProvider is not null ? new ModelProviderResolver(new Dictionary<string, IModelProvider>
        {
            ["chat_completions"] = singleProvider,
            ["openai_responses"] = singleProvider,
            ["anthropic_messages"] = singleProvider,
        }) : throw new ArgumentNullException(nameof(providers)));
        _options = options ?? DefaultOptions;
    }

    public async Task<WorkerV2StepResult> RunAsync(JsonObject startPayload, CancellationToken cancellationToken)
        => await RunAsync(startPayload, null, cancellationToken).ConfigureAwait(false);

    public async Task<WorkerV2StepResult> RunAsync(
        JsonObject startPayload,
        Func<string, ValueTask>? onDelta,
        CancellationToken cancellationToken,
        string? modelStepId = null)
    {
        ArgumentNullException.ThrowIfNull(startPayload);
        WorkerV2StartRequest start;
        try
        {
            start = WorkerV2PromptCodec.ReadStart(startPayload);
        }
        catch (RuAgent.Protocol.AgentProtocolException error)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false, error);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            DeleteOwnedProviderBootstrap(ReadProviderBootstrapPath(startPayload));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var providerBootstrap = ResolveProviderBootstrap(
            ReadProviderBootstrapPath(startPayload),
            cancellationToken);
        // Only the OpenAI-compatible adapter exposes the host capabilities
        // through provider-native function calling; other protocols keep the
        // text selection contract.
        var nativeToolCalling = string.Equals(
            providerBootstrap.Protocol,
            ProviderProtocols.ChatCompletions,
            StringComparison.Ordinal);
        var snapshot = SystemPromptComposer.Compose(start.Context, nativeToolCalling);
        var requestTurns = nativeToolCalling
            ? start.EffectiveTurns
            : WorkerV2PromptCodec.ProjectTextTurns(start.EffectiveTurns, start.Settings);
        var request = new ModelRequest(
            providerBootstrap.Model,
            snapshot.Text,
            start.Prompt,
            requestTurns,
            BuildRequestSettings(start.Settings, providerBootstrap.RequestOptions),
            providerBootstrap.Endpoint,
            providerBootstrap.Credential,
            providerBootstrap.MaximumOutputTokens,
            start.Images ?? Array.Empty<ModelImage>());
        var holdback = new DeltaHoldback(onDelta);

        var providerGateEntered = false;
        try
        {
            await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            providerGateEntered = true;
            var text = new StringBuilder();
            var reasoning = new StringBuilder();
            var toolFragments = new List<ModelToolFragment>();
            string? finishReason = null;
            ModelUsage? usage = null;
            IModelProvider provider;
            try { provider = _providers.Resolve(providerBootstrap.Protocol); }
            catch (ArgumentException ex) { throw new WorkerV2StepException("provider_bootstrap_invalid", false, ex); }
            try
            {
                // A completed delta ends the *content* of the step, not the
                // stream: include_usage sends its tail chunk after finish_reason,
                // so the enumeration keeps draining until the provider closes,
                // and only usage information still reaches the step.
                var stepCompleted = false;
                await foreach (var delta in provider.GenerateAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
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

                    if (delta.Text is not null)
                    {
                        text.Append(delta.Text);
                        if (text.Length > 1_000_000)
                        {
                            throw new WorkerV2StepException("protocol_failure", retryable: false);
                        }

                        await holdback.ForwardAsync(delta.Text).ConfigureAwait(false);
                    }

                    toolFragments.AddRange(delta.ToolFragments);
                    // Long argument streams arrive as one fragment per SSE chunk;
                    // the per-chunk shape limit lives in the adapter. This cap only
                    // bounds memory, and the byte cap on reassembled arguments
                    // (model_tool_call_invalid) is what bounds the call itself.
                    if (toolFragments.Count > 2048)
                    {
                        throw new WorkerV2StepException("protocol_failure", retryable: false);
                    }

                    if (delta.Reasoning is not null)
                    {
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
        var providerStepId = string.IsNullOrWhiteSpace(modelStepId)
            ? DirectModelStepId
            : modelStepId;
        var (calls, visibleText) = ParseToolCalls(toolFragments, start.Settings, finishReason, text, providerStepId);
        if (calls.Count > 0)
        {
            finishReason = "tool_calls";
        }
        if (calls.Count == 0)
        {
            await holdback.ReleaseAsync().ConfigureAwait(false);
        }

        return new WorkerV2StepResult(visibleText, calls, finishReason, snapshot.Receipt, usage, ExtractReasoningText(reasoning, text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerV2StepException)
        {
            throw;
        }
        catch (ModelProviderException error)
        {
            throw MapProviderException(error);
        }
        catch (Exception error)
        {
            throw new WorkerV2StepException("generation_failed", retryable: true, error);
        }
        finally
        {
            if (providerGateEntered)
            {
                _providerGate.Release();
            }
        }
    }

    private static string? ExtractReasoningText(StringBuilder reasoning, StringBuilder text)
    {
        if (reasoning.Length > 0)
        {
            var excerpt = reasoning.ToString().Trim();
            return excerpt.Length == 0 ? null : excerpt;
        }

        return ExtractThinkBlocks(text.ToString());
    }

    private static string? ExtractThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var collected = new StringBuilder();
        int searchFrom = 0;
        while (true)
        {
            int open = text.IndexOf("<think>", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf("</think>", open + "<think>".Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }

            collected.Append(text.Substring(open + "<think>".Length, close - open - "<think>".Length).Trim());
            collected.Append('\n');
            searchFrom = close + "</think>".Length;
            if (collected.Length >= MaximumReasoningChars)
            {
                break;
            }
        }

        if (collected.Length == 0)
        {
            return null;
        }

        // Keep the tail: the final deliberation is the most relevant excerpt.
        var excerpt = collected.ToString().Trim();
        if (excerpt.Length > MaximumReasoningChars)
        {
            excerpt = excerpt[^MaximumReasoningChars..];
        }

        return excerpt.Length == 0 ? null : excerpt;
    }

    private static (List<ModelToolCall> Calls, string? Text) ParseToolCalls(
        IReadOnlyList<ModelToolFragment> fragments,
        JsonObject settings,
        string finishReason,
        StringBuilder text,
        string modelStepId)
    {
        if (fragments.Count == 0)
        {
            var (promptSelection, visibleText) = ParsePromptToolSelection(text.ToString(), settings, modelStepId);
            if (promptSelection is not null)
            {
                return ([promptSelection], visibleText);
            }

            if (finishReason == "tool_calls")
            {
                throw new WorkerV2StepException("protocol_failure", retryable: false);
            }

            // VisibleText already carries the stripped prose when a trailing
            // block was removed; otherwise it is the full text (or empty).
            return (new List<ModelToolCall>(), string.IsNullOrEmpty(visibleText) ? null : visibleText);
        }

        var groups = new List<CallGroup> { new() };
        foreach (var fragment in fragments)
        {
            var hasProviderCallId = !string.IsNullOrEmpty(fragment.ProviderCallId);
            var opensNewCall = hasProviderCallId
                && (groups[^1].ProviderCallId is not null
                    || groups[^1].ToolName is not null
                    || groups[^1].Arguments.Length > 0);
            if (opensNewCall)
            {
                groups.Add(new CallGroup());
            }

            var group = groups[^1];
            if (!hasProviderCallId)
            {
                var hasExistingCallData = group.ToolName is not null || group.Arguments.Length > 0;
                if (groups.Count > 1 || (hasExistingCallData && !string.IsNullOrEmpty(fragment.ToolName)))
                {
                    throw new WorkerV2StepException("protocol_failure", retryable: false);
                }
            }

            if (hasProviderCallId)
            {
                group.ProviderCallId = fragment.ProviderCallId;
            }

            if (!string.IsNullOrEmpty(fragment.ToolName))
            {
                group.ToolName ??= fragment.ToolName;
            }

            group.Arguments.Append(fragment.ArgumentsFragment);
        }

        var calls = new List<ModelToolCall>(groups.Count);
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.ToolName))
            {
                throw new WorkerV2StepException("protocol_failure", retryable: true);
            }

            var tool = settings["model_tools"] is JsonArray modelTools
                ? modelTools.OfType<JsonObject>().FirstOrDefault(item => item["tool_id"]?.GetValue<string>() == group.ToolName)
                : null;
            var toolVersion = tool?["tool_version"]?.GetValue<string>();
            if (toolVersion is null)
            {
                throw new WorkerV2StepException("model_tool_call_unavailable", retryable: true);
            }

            var argumentsText = group.Arguments.ToString();
            if (Encoding.UTF8.GetByteCount(argumentsText) > 64 * 1024)
            {
                throw new WorkerV2StepException("model_tool_call_invalid", retryable: true);
            }

            JsonElement arguments;
            try
            {
                using var document = JsonDocument.Parse(argumentsText);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("arguments must be an object");
                }

                arguments = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw new WorkerV2StepException("model_tool_call_invalid", retryable: true, error);
            }

            var providerCallId = group.ProviderCallId
                ?? $"ru_call_{modelStepId}_{calls.Count + 1}";

            try
            {
                calls.Add(new ModelToolCall(providerCallId, group.ToolName, toolVersion, arguments));
            }
            catch (ArgumentException error)
            {
                throw new WorkerV2StepException("protocol_failure", retryable: false, error);
            }
        }

        try
        {
            // Provider-native calls may carry narration before the call. It has already
            // streamed as deltas and it belongs to the step: dropping it here was the only
            // reason the feed stayed silent until the run finished. Raw call markup stays
            // out — narration is prose, never the call written out by hand.
            var narration = text.ToString().Trim();
            return (calls, narration.Length == 0 || IsToolCallMarkup(narration) ? null : narration);
        }
        catch (ArgumentException error)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false, error);
        }
    }

    private sealed class CallGroup
    {
        public string? ProviderCallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// Whether step text is the tool call written out by hand rather than prose.
    /// </summary>
    /// <remarks>
    /// Prompt-based selections already strip their call block before it can be seen; a native
    /// call whose text side is markup gets the same treatment, because raw call markup must
    /// never reach the chat whichever way the model chose to write it.
    /// </remarks>
    private static bool IsToolCallMarkup(string text)
    {
        var candidate = ModelToolSelectionParser.NormalizeCandidate(text);
        return candidate.Length != 0 && candidate[0] is '{' or '[';
    }

    private static (ModelToolCall? Call, string? VisibleText) ParsePromptToolSelection(
        string text,
        JsonObject settings,
        string modelStepId)
    {
        var modelTools = settings["model_tools"] as JsonArray;
        var hasTools = modelTools is { Count: > 0 };
        var advertised = AdvertisedToolIds(modelTools);

        var candidate = ModelToolSelectionParser.NormalizeCandidate(text);
        if (candidate.Length != 0 && candidate[0] is '{' or '[')
        {
            if (!hasTools && IsJsonObjectWithoutToolFields(candidate))
            {
                return (null, text);
            }

            return (BuildSelectionCall(modelTools, candidate, advertised, modelStepId), null);
        }

        // Trailing wrapper: prose first, tool call last. A complete trailing
        // block either executes (valid selection) or is stripped from visible
        // text (invalid selection) so raw tool-call markup never reaches chat.
        if (ModelToolSelectionParser.TrySplitTrailingBlock(text, out var prose, out var block))
        {
            try
            {
                var trailing = ModelToolSelectionParser.Parse(
                    ModelToolSelectionParser.NormalizeCandidate(block), advertised);
                return (BuildSelectionCall(modelTools, trailing, modelStepId), string.IsNullOrWhiteSpace(prose) ? null : prose);
            }
            catch (ModelToolSelectionException error) when (
                error.Code is "unknown_tool" or "invalid_tool_version")
            {
                throw new WorkerV2StepException("capability_unavailable", retryable: false, error);
            }
            catch (ModelToolSelectionException error)
            {
                if (string.IsNullOrWhiteSpace(prose))
                {
                    throw new WorkerV2StepException("invalid_selection", retryable: true, error);
                }

                return (null, prose);
            }
        }

        // Opener-led attempt anywhere (prose glued to an envelope, possibly with
        // mangled delimiters). A tool-keyed payload executes with the prose
        // kept visible; a broken tool-keyed payload fails loudly instead of
        // leaking machine syntax as a chat answer. Anything without a
        // tool-keyed payload falls through (discussion/trailing paths below).
        if (ModelToolSelectionParser.TryExtractCallCandidate(text, out var callProse, out var callJson))
        {
            try
            {
                var selected = ModelToolSelectionParser.Parse(
                    ModelToolSelectionParser.NormalizeCandidate(callJson), advertised);
                return (BuildSelectionCall(modelTools, selected, modelStepId),
                    string.IsNullOrWhiteSpace(callProse) ? null : callProse);
            }
            catch (ModelToolSelectionException error) when (
                error.Code is "unknown_tool" or "invalid_tool_version")
            {
                throw new WorkerV2StepException("capability_unavailable", retryable: false, error);
            }
            catch (ModelToolSelectionException error)
            {
                throw new WorkerV2StepException("invalid_selection", retryable: true, error);
            }
        }

        if (!hasTools && IsJsonObjectWithoutToolFields(candidate))
        {
            return (null, text);
        }

        // A lone tool-call attempt in a non-JSON dialect (e.g. XML-ish tags the
        // model invented instead of the JSON envelope) must fail loudly instead
        // of being presented as a chat answer: the model tried to act, so
        // silent prose would mislead the user into thinking it answered.
        // Mid-text syntax discussion (tag not leading) stays visible.
        if (text.TrimStart().StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerV2StepException("invalid_selection", retryable: true);
        }

        return (null, text);
    }

    private static HashSet<string> AdvertisedToolIds(JsonArray? modelTools) =>
        modelTools?
            .OfType<JsonObject>()
            .Select(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                    ? id
                    : string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    private static ModelToolCall BuildSelectionCall(
        JsonArray? modelTools,
        string candidate,
        HashSet<string> advertised,
        string modelStepId)
    {
        ModelToolSelection selection;
        try
        {
            selection = ModelToolSelectionParser.Parse(candidate, advertised);
        }
        catch (ModelToolSelectionException error) when (
            error.Code is "unknown_tool" or "invalid_tool_version")
        {
            throw new WorkerV2StepException("capability_unavailable", retryable: false, error);
        }
        catch (ModelToolSelectionException error)
        {
            throw new WorkerV2StepException("invalid_selection", retryable: true, error);
        }

        return BuildSelectionCall(modelTools, selection, modelStepId);
    }

    private static ModelToolCall BuildSelectionCall(
        JsonArray? modelTools,
        ModelToolSelection selection,
        string modelStepId)
    {
        var configuredTool = modelTools?
            .OfType<JsonObject>()
            .FirstOrDefault(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                && string.Equals(id, selection.ToolId, StringComparison.Ordinal));
        var configuredVersion = configuredTool?["tool_version"] is JsonValue version
            && version.TryGetValue<string>(out var versionText)
                ? versionText
                : null;
        if (!string.Equals(configuredVersion, selection.ToolVersion, StringComparison.Ordinal))
        {
            throw new WorkerV2StepException("capability_unavailable", retryable: false);
        }

        try
        {
            var providerCallId = $"ru_call_{modelStepId}_1";
            return new ModelToolCall(providerCallId, selection.ToolId, selection.ToolVersion, selection.Arguments);
        }
        catch (ArgumentException error)
        {
            throw new WorkerV2StepException("invalid_selection", retryable: true, error);
        }
    }

    private static bool IsJsonObjectWithoutToolFields(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return !document.RootElement.EnumerateObject().Any(property =>
                property.Name is "tool_id" or "tool_version" or "arguments");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static WorkerV2StepException MapProviderException(ModelProviderException error) => error.Code switch
    {
        "timeout" => new WorkerV2StepException("provider_timeout", retryable: true, error),
        "auth_failed" or "forbidden" => new WorkerV2StepException("provider_auth", retryable: false, error),
        "rate_limited" => new WorkerV2StepException("provider_rate_limited", retryable: true, error),
        "server_error" => new WorkerV2StepException("provider_server_error", retryable: true, error),
        "transport_error" => new WorkerV2StepException("provider_transport_error", retryable: true, error),
        "malformed_payload" or "response_too_large" => new WorkerV2StepException("protocol_failure", retryable: false, error),
        _ => new WorkerV2StepException("generation_failed", error.Retryable, error),
    };

    private ProviderBootstrapData ResolveProviderBootstrap(
        string? providerBootstrapPath,
        CancellationToken cancellationToken)
    {
        if (_options.ProviderBootstrapRoot is null)
        {
            return new ProviderBootstrapData(
                "startup",
                ProviderProtocols.ChatCompletions,
                _options.ProviderEndpoint,
                _options.ProviderModel,
                _options.ProviderCredential);
        }

        if (string.IsNullOrWhiteSpace(providerBootstrapPath))
        {
            throw new WorkerV2StepException("provider_bootstrap_required", retryable: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ProtectedProviderBootstrap.ReadAndDelete(
                _options.ProviderBootstrapRoot,
                providerBootstrapPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new WorkerV2StepException("provider_bootstrap_invalid", retryable: false);
        }
    }

    private static string? ReadProviderBootstrapPath(JsonObject startPayload) =>
        startPayload["provider_bootstrap_path"] is JsonValue value
            && value.TryGetValue<string>(out var path)
            ? path
            : null;

    private static JsonObject BuildRequestSettings(
        JsonObject untrustedSettings,
        JsonObject? protectedRequestOptions)
    {
        var settings = (JsonObject)untrustedSettings.DeepClone();
        foreach (var field in UntrustedProviderOptionFields)
        {
            settings.Remove(field);
        }

        if (protectedRequestOptions is not null)
        {
            foreach (var property in protectedRequestOptions)
            {
                settings[property.Key] = property.Value?.DeepClone();
            }
        }

        return settings;
    }

    private void DeleteOwnedProviderBootstrap(string? path)
    {
        if (_options.ProviderBootstrapRoot is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            ProtectedProviderBootstrap.Delete(_options.ProviderBootstrapRoot, path);
        }
        catch
        {
            // Never replace cancellation with a cleanup or path-validation error.
        }
    }

    private static readonly IReadOnlySet<string> UntrustedProviderOptionFields =
        new HashSet<string>(
            ["reasoning", "reasoning_effort", "reasoning_model", "kilo_reasoning_enabled", "api_variant", "max_tokens"],
            StringComparer.Ordinal);

    private sealed class DeltaHoldback
    {
        private const string ToolCallOpenTag = "<tool_call>";
        private const string ToolCallCloseTag = "</tool_call>";
        private const int TagOverlapChars = 10;

        private readonly Func<string, ValueTask>? _sink;
        private readonly List<string> _held = [];
        private bool _decided;
        private bool _structured;
        private long _bytes;
        private string _tail = string.Empty;
        private bool _tailActive;
        private string _recent = string.Empty;

        public DeltaHoldback(Func<string, ValueTask>? sink)
        {
            _sink = sink;
        }

        public async ValueTask ForwardAsync(string delta)
        {
            if (_sink is null || delta.Length == 0)
            {
                return;
            }

            _bytes += Encoding.UTF8.GetByteCount(delta);
            if (_bytes > 1_000_000)
            {
                throw new WorkerV2StepException("protocol_failure", retryable: false);
            }

            var overlap = _recent;
            _recent = TailOverlap(_recent + delta);

            if (_decided)
            {
                if (!_structured)
                {
                    await ForwardProseAsync(delta, overlap).ConfigureAwait(false);
                }

                return;
            }

            _held.Add(delta);
            var combined = string.Concat(_held);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return;
            }

            var trimmed = combined.TrimStart();
            if (CouldBeStructuredSelection(trimmed))
            {
                _structured = true;
                return;
            }

            _decided = true;
            var tagAt = combined.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (tagAt < 0)
            {
                foreach (var held in _held)
                {
                    await _sink(held).ConfigureAwait(false);
                }
            }
            else
            {
                // Prose arrived together with a later tool-call candidate in one
                // chunk: show only the prose, hold the candidate for end-of-step
                // validation like the streaming path does.
                if (tagAt > 0)
                {
                    await _sink(combined[..tagAt]).ConfigureAwait(false);
                }

                _tail = combined[tagAt..];
                _tailActive = true;
            }

            _held.Clear();
        }

        public async ValueTask ReleaseAsync()
        {
            if (_sink is not null)
            {
                if (_tailActive)
                {
                    // End of step with a held candidate tail. A complete
                    // trailing block is either valid (the final parse executed
                    // it, so this is unreachable) or invalid (drop it to match
                    // the stripped final text). An unterminated tail is
                    // ordinary prose: flush it.
                    if (!ModelToolSelectionParser.TrySplitTrailingBlock(_tail, out _, out _))
                    {
                        await _sink(_tail).ConfigureAwait(false);
                    }

                    _tail = string.Empty;
                    _tailActive = false;
                }
                else if (_structured)
                {
                    foreach (var held in _held)
                    {
                        await _sink(held).ConfigureAwait(false);
                    }
                }
            }

            _held.Clear();
            _decided = true;
            _structured = false;
        }

        private async ValueTask ForwardProseAsync(string delta, string overlap)
        {
            if (!_tailActive)
            {
                var scan = overlap + delta;
                var tagAt = scan.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
                if (tagAt < 0)
                {
                    await _sink!(delta).ConfigureAwait(false);
                    return;
                }

                // A tool-call candidate starts mid-stream (possibly split
                // across deltas, reconstructed via overlap): show the prose
                // before it and hold the candidate for end-of-step validation.
                var sinkCount = tagAt - overlap.Length;
                if (sinkCount > 0)
                {
                    await _sink!(delta[..sinkCount]).ConfigureAwait(false);
                }

                _tail = scan[tagAt..];
                _tailActive = true;
                return;
            }

            _tail += delta;
            // A candidate stops being trailing as soon as real prose follows
            // its closing tag: flush everything, it was discussion, not a call.
            var closeAt = _tail.LastIndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase);
            if (closeAt >= 0
                && !string.IsNullOrWhiteSpace(_tail[(closeAt + ToolCallCloseTag.Length)..]))
            {
                await _sink!(_tail).ConfigureAwait(false);
                _tail = string.Empty;
                _tailActive = false;
            }
        }

        private static string TailOverlap(string value) =>
            value.Length <= TagOverlapChars ? value : value[^TagOverlapChars..];

        private static bool CouldBeStructuredSelection(string candidate)
        {
            if (candidate[0] is '{' or '[')
            {
                return true;
            }

            const string toolCallTag = "<tool_call>";
            if (candidate[0] == '<'
                && (toolCallTag.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(toolCallTag, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // A fenced block can carry the tool envelope (```json ... ```), which the
            // final parse unwraps and executes. Hold it for end-of-step validation
            // like other structured candidates so raw tool JSON is never streamed
            // as chat text. Partial fences (`/``) are held too; prose starting
            // with a single backtick plus text still streams immediately.
            if (candidate[0] == '`')
            {
                if (candidate.Length < 3 && candidate.Trim('`').Length == 0)
                {
                    return true;
                }

                if (candidate.StartsWith("```", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
