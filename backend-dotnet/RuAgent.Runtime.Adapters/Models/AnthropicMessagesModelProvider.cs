using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class AnthropicMessagesModelProvider : IModelProvider, IModelRequestBodyProjector
{
    /// <summary>
    /// The largest output cap this adapter will send. The limit is a property of
    /// the adapter's contract, so the bootstrap validation and the backend's
    /// snapshot factory both reference it instead of repeating the literal.
    /// </summary>
    public const int MaximumSupportedOutputTokens = 128_000;

    private readonly HttpClient _http;
    private readonly TimeSpan? _requestTimeout;
    public AnthropicMessagesModelProvider(HttpClient http)
        : this(http, requestTimeout: null)
    {
    }

    public AnthropicMessagesModelProvider(HttpClient http, TimeSpan? requestTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _requestTimeout = requestTimeout;
    }

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumOutputTokens is null or <= 0 or > MaximumSupportedOutputTokens) throw new ModelProviderException("malformed_payload", false);
        var body = request.PreparedBodyJson ?? BuildBody(request);
        var nativeContent = new AnthropicNativeContentAssembly();
        ModelUsage? usage = null;
        // A provider-hosted search may pause a long turn (ADR 0033); the paused content is
        // sent back and the same model step continues, a bounded number of times.
        for (var pauses = 0; ; pauses++)
        {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", request.Credential);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var response = await ModelHttpTransport.SendAsync(
            _http, message, cancellationToken, _requestTimeout).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ModelContextOverflowError.ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                if (ModelContextOverflowError.IsError(error)) throw new ModelProviderException(ModelContextOverflowError.Code, false);
                if (request.ProviderWebSearch && ModelContextOverflowError.IsWebSearchUnavailable(error))
                    throw new ModelProviderException(ModelContextOverflowError.WebSearchUnavailableCode, false);
                throw MapStatus(response.StatusCode, ModelRateLimitError.GetRetryAfter(response));
            }
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ModelProviderException("timeout", true); }
            using (stream)
            {
                var phase = Phase.ExpectMessageStart;
                string? stopReason = null;
                int? blockIndex = null;
                var nextBlockIndex = 0;
                string blockType = string.Empty;
                var paused = false;

                await foreach (var evt in BoundedSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evt.Event == "ping")
                    {
                        continue;
                    }
                    if (evt.Event is null) throw new ModelProviderException("malformed_payload", false);
                    if (evt.Data.Length == 0) throw new ModelProviderException("malformed_payload", false);
                    JsonObject data;
                    try { data = JsonNode.Parse(evt.Data) as JsonObject ?? throw new JsonException(); }
                    catch (JsonException ex) { throw new ModelProviderException("malformed_payload", false, ex); }

                    var type = ReadString(data["type"]);
                    if (type is null) throw new ModelProviderException("malformed_payload", false);
                    if (type != evt.Event)
                    {
                        if (IsKnownEvent(type) || IsKnownEvent(evt.Event))
                            throw new ModelProviderException("malformed_payload", false);
                        continue;
                    }

                    switch (type)
                    {
                        case "message_start":
                        {
                            if (phase != Phase.ExpectMessageStart) throw new ModelProviderException("malformed_payload", false);
                            var messageData = ReadObject(data["message"])
                                ?? throw new ModelProviderException("malformed_payload", false);
                            var model = ReadString(messageData["model"]);
                            if (model is null || model != request.Model) throw new ModelProviderException("malformed_payload", false);
                            usage = MergeUsage(usage, ReadUsage(messageData["usage"], request.Model));
                            var content = ReadArray(messageData["content"]);
                            if (content is not null)
                                foreach (var block in content)
                                {
                                    var contentBlock = ReadObject(block)
                                        ?? throw new ModelProviderException("malformed_payload", false);
                                    if (ReadString(contentBlock["type"]) == "fallback")
                                        throw new ModelProviderException("malformed_payload", false);
                                }
                            phase = Phase.InMessage;
                            break;
                        }
                        case "content_block_start":
                        {
                            if (phase is not Phase.InMessage and not Phase.InBlock) throw new ModelProviderException("malformed_payload", false);
                            var idx = ReadInt(data["index"]);
                            if (idx is null || idx != nextBlockIndex) throw new ModelProviderException("malformed_payload", false);
                            if (blockIndex is not null) throw new ModelProviderException("malformed_payload", false);
                            var block = data["content_block"] as JsonObject ?? throw new ModelProviderException("malformed_payload", false);
                            var t = ReadString(block["type"]) ?? throw new ModelProviderException("malformed_payload", false);
                            if (t == "fallback" || (!request.NativeToolCalling && t is "tool_use" or "redacted_thinking")) throw new ModelProviderException("malformed_payload", false);
                            var searchBlock = t is "server_tool_use" or "web_search_tool_result";
                            if (searchBlock && !request.ProviderWebSearch) throw new ModelProviderException("malformed_payload", false);
                            if (!searchBlock && t is not ("text" or "thinking" or "redacted_thinking" or "tool_use")) throw new ModelProviderException("malformed_payload", false);
                            nativeContent.BeginBlock(block, t, request.NativeToolCalling);
                            if (t == "text")
                            {
                                if (nativeContent.Text.Length > 0) yield return new ModelDelta(nativeContent.Text, [], false, null);
                            }
                            else if (t == "thinking")
                            {
                                if (!request.NativeToolCalling && nativeContent.Text.Length > 0)
                                    yield return new ModelDelta(null, [], false, null, null, nativeContent.Text);
                            }
                            blockIndex = idx;
                            nextBlockIndex++;
                            blockType = t;
                            phase = Phase.InBlock;
                            break;
                        }
                        case "content_block_delta":
                        {
                            if (phase != Phase.InBlock || blockIndex is null) throw new ModelProviderException("malformed_payload", false);
                            var idx = ReadInt(data["index"]);
                            if (idx != blockIndex) throw new ModelProviderException("malformed_payload", false);
                            var delta = data["delta"] as JsonObject ?? throw new ModelProviderException("malformed_payload", false);
                            var deltaType = ReadString(delta["type"]) ?? throw new ModelProviderException("malformed_payload", false);
                            if (deltaType == "text_delta")
                            {
                                if (blockType != "text") throw new ModelProviderException("malformed_payload", false);
                                var text = ReadString(delta["text"]) ?? throw new ModelProviderException("malformed_payload", false);
                                if (text.Length == 0) throw new ModelProviderException("malformed_payload", false);
                                nativeContent.AppendText(text, request.NativeToolCalling);
                                yield return new ModelDelta(text, [], false, null);
                            }
                            else if (deltaType == "thinking_delta")
                            {
                                if (blockType != "thinking") throw new ModelProviderException("malformed_payload", false);
                                var thinking = ReadString(delta["thinking"])
                                    ?? throw new ModelProviderException("malformed_payload", false);
                                nativeContent.AppendThinking(thinking, request.NativeToolCalling);
                                if (!request.NativeToolCalling && thinking.Length > 0)
                                {
                                    yield return new ModelDelta(null, [], false, null, null, thinking);
                                }
                            }
                            else if (deltaType == "signature_delta")
                            {
                                if (blockType != "thinking") throw new ModelProviderException("malformed_payload", false);
                                var signature = ReadString(delta["signature"]) ?? throw new ModelProviderException("malformed_payload", false);
                                if (request.NativeToolCalling)
                                {
                                    nativeContent.AppendSignature(signature, isNative: true);
                                }
                            }
                            else if (deltaType == "citations_delta")
                            {
                                if (blockType != "text" || !request.ProviderWebSearch) throw new ModelProviderException("malformed_payload", false);
                                var citation = delta["citation"] as JsonObject ?? throw new ModelProviderException("malformed_payload", false);
                                nativeContent.AppendCitation(citation, request.NativeToolCalling);
                            }
                            else if (deltaType == "input_json_delta")
                            {
                                if (blockType is not ("tool_use" or "server_tool_use") || !request.NativeToolCalling) throw new ModelProviderException("malformed_payload", false);
                                var partial = ReadString(delta["partial_json"]) ?? throw new ModelProviderException("malformed_payload", false);
                                nativeContent.AppendInputJson(partial);
                            }
                            else throw new ModelProviderException("malformed_payload", false);
                            break;
                        }
                        case "content_block_stop":
                        {
                            if (phase != Phase.InBlock || blockIndex is null) throw new ModelProviderException("malformed_payload", false);
                            var idx = ReadInt(data["index"]);
                            if (idx != blockIndex) throw new ModelProviderException("malformed_payload", false);
                            if (request.NativeToolCalling && blockType == "tool_use")
                            {
                                nativeContent.CompleteBlock(isNative: true);
                                yield return new ModelDelta(null, [nativeContent.ToolFragments[^1]], false, null);
                            }
                            else nativeContent.CompleteBlock(request.NativeToolCalling);
                            blockIndex = null;
                            blockType = string.Empty;
                            phase = Phase.InMessage;
                            break;
                        }
                        case "message_delta":
                        {
                            if (phase != Phase.InMessage || blockIndex is not null) throw new ModelProviderException("malformed_payload", false);
                            var delta = ReadObject(data["delta"])
                                ?? throw new ModelProviderException("malformed_payload", false);
                            var reason = ReadString(delta["stop_reason"]);
                            if (reason is null) throw new ModelProviderException("malformed_payload", false);
                            if (reason == "tool_use" && (!request.NativeToolCalling || !nativeContent.HasTools)) throw new ModelProviderException("malformed_payload", false);
                            paused = reason == "pause_turn" && request.ProviderWebSearch && !nativeContent.HasTools;
                            if (!paused && reason is not ("end_turn" or "stop_sequence" or "max_tokens" or "tool_use")) throw new ModelProviderException("malformed_payload", false);
                            if (nativeContent.HasTools && reason != "tool_use") throw new ModelProviderException("malformed_payload", false);
                            usage = MergeUsage(usage, ReadUsage(data["usage"], request.Model));
                            stopReason = reason == "max_tokens" ? "length" : reason == "tool_use" ? "tool_calls" : "stop";
                            phase = Phase.GotDelta;
                            break;
                        }
                        case "message_stop":
                        {
                            if (phase != Phase.GotDelta) throw new ModelProviderException("malformed_payload", false);
                            if (paused)
                            {
                                phase = Phase.Paused;
                                break;
                            }
                            var continuation = request.NativeToolCalling ? nativeContent.CreateContinuation(request.Model) : null;
                            // Answers that cite web results show their sources (ADR 0033); the
                            // provider requires it, and the list survives into later history.
                            var sources = nativeContent.HasTools ? string.Empty
                                : ProviderSearchContent.FormatSources(ProviderSearchContent.CitedSources(nativeContent.Citations));
                            if (sources.Length > 0) yield return new ModelDelta(sources, [], false, null);
                            yield return new ModelDelta(null, [], true, stopReason ?? "stop", usage, providerContinuation: continuation);
                            yield break;
                        }
                        case "error":
                        {
                            var error = ReadObject(data["error"]);
                            if (ModelContextOverflowError.IsError(error))
                                throw new ModelProviderException(ModelContextOverflowError.Code, false);
                            var errType = ReadString(error?["type"]) ?? string.Empty;
                            var mapped = errType switch
                            {
                                "overloaded_error" => new ModelProviderException("server_error", true),
                                "rate_limit_error" => new ModelProviderException("rate_limited", true),
                                // The stream-level twin of a 5xx response: the request
                                // reached Anthropic and the answer failed, so it keeps
                                // the HTTP mapping's retryability instead of failing
                                // the run closed on the first occurrence.
                                "api_error" => new ModelProviderException("server_error", true),
                                _ => new ModelProviderException("http_error", false),
                            };
                            throw mapped;
                        }
                        default: continue;
                    }
                    if (phase == Phase.Paused) break;
                }
                if (phase != Phase.Paused) throw new ModelProviderException("malformed_payload", false);
            }
        }
        if (pauses >= MaximumPausedTurnContinuations) throw new ModelProviderException("provider_search_paused", false);
        body = AppendPausedTurn(body, nativeContent.ContentForResend());
        }
    }

    /// <summary>How often one model step resends a turn paused by a provider-hosted search.</summary>
    public const int MaximumPausedTurnContinuations = 3;

    private static string AppendPausedTurn(string body, JsonArray content)
    {
        var request = JsonNode.Parse(body) as JsonObject ?? throw new ModelProviderException("malformed_payload", false);
        if (request["messages"] is not JsonArray messages) throw new ModelProviderException("malformed_payload", false);
        // The whole turn so far, unchanged: the API continues it from there.
        if (messages.Count > 0 && messages[^1] is JsonObject last && last["role"]?.GetValue<string>() == "assistant")
            messages.RemoveAt(messages.Count - 1);
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
        var resent = request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (Encoding.UTF8.GetByteCount(resent) > ModelRequest.MaximumSerializedBodyBytes)
            throw new ModelProviderException("response_too_large", false);
        return resent;
    }

    private enum Phase { ExpectMessageStart, InMessage, InBlock, GotDelta, Paused }

    private static bool IsKnownEvent(string value) => value is
        "message_start" or "content_block_start" or "content_block_delta" or
        "content_block_stop" or "message_delta" or "message_stop" or "error";

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static ModelUsage? ReadUsage(JsonNode? node, string? model)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is not JsonObject usage)
            throw new ModelProviderException("malformed_payload", false);

        var input = ReadOptionalInt(usage["input_tokens"]);
        var output = ReadOptionalInt(usage["output_tokens"]);
        var cacheRead = ReadOptionalInt(usage["cache_read_input_tokens"]);
        var cacheWrite = ReadOptionalInt(usage["cache_creation_input_tokens"]);
        if (input is null && output is null && cacheRead is null && cacheWrite is null)
            return null;
        return new ModelUsage(input, output, null, cacheRead, cacheWrite, model, inputTokensIncludeCache: false);
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0)
            return count;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static ModelUsage? MergeUsage(ModelUsage? first, ModelUsage? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return new ModelUsage(
            second.InputTokens ?? first.InputTokens,
            second.OutputTokens ?? first.OutputTokens,
            second.TotalTokens ?? first.TotalTokens,
            second.CacheReadInputTokens ?? first.CacheReadInputTokens,
            second.CacheWriteInputTokens ?? first.CacheWriteInputTokens,
            // A streamed request reports usage twice for one model, so both halves
            // name the same one. Keeping the first that named it means a later chunk
            // without the field cannot erase it.
            first.Model ?? second.Model,
            inputTokensIncludeCache: false);
    }

    private static JsonObject? ReadObject(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject value) return value;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static JsonArray? ReadArray(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonArray value) return value;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static string BuildBody(ModelRequest request)
    {
        if (request.Images.Count > 0 && !ModelImage.IsVisionModel(request.Model))
            throw new ModelProviderException("vision_unsupported", false);

        var messages = new JsonArray();
        if (request.NativeToolCalling)
        {
            foreach (var item in request.AnthropicMessages)
                messages.Add(item.DeepClone());
        }
        else
        {
            foreach (var turn in request.Turns)
            {
                if (turn.Role is not ("user" or "assistant"))
                    throw new ModelProviderException("malformed_payload", false);
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
            }
        }

        if (!string.IsNullOrEmpty(request.RuntimeContext))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.RuntimeContext });

        if (request.Images.Count > 0)
        {
            var content = new JsonArray();
            foreach (var img in request.Images)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = img.MimeType,
                        ["data"] = img.Base64Data,
                    }
                });
            }
            content.Add(new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        else if (!request.PromptInHistory)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["system"] = request.SystemPrompt,
            ["messages"] = messages,
            ["stream"] = true,
            ["max_tokens"] = request.MaximumOutputTokens!.Value,
        };
        if (UsesAutomaticPromptCache(request.Endpoint))
            body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (request.Settings["thinking"] is JsonObject thinking) body["thinking"] = (JsonObject)thinking.DeepClone();
        if (request.Settings["output_config"] is JsonObject oc) body["output_config"] = (JsonObject)oc.DeepClone();
        if (request.NativeToolCalling)
        {
            var tools = BuildTools(request.Settings["model_tools"] as JsonArray);
            if (request.ProviderWebSearch)
            {
                // The provider's hosted search replaces Ru's web_search for this run (ADR 0033).
                if (tools.OfType<JsonObject>().Any(tool => tool["name"]?.GetValue<string>() == ProviderSearchContent.AnthropicToolName))
                    throw new ModelProviderException("malformed_payload", false);
                tools.Add(new JsonObject
                {
                    ["type"] = WebSearchToolType,
                    ["name"] = ProviderSearchContent.AnthropicToolName,
                    ["max_uses"] = WebSearchMaxUses,
                });
            }
            if (tools.Count > 0)
            {
                body["tools"] = tools;
            }
        }

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public string ProjectRequestBody(ModelRequest request) => BuildBody(request);

    /// <summary>The basic hosted search: direct calls, no code-execution step (ADR 0033).</summary>
    internal const string WebSearchToolType = "web_search_20250305";
    internal const int WebSearchMaxUses = 5;

    /// <summary>
    /// Anthropic caches a prompt only when the request asks for it. Agent requests keep
    /// their prefix stable across steps, so a top-level <c>cache_control</c> lets each
    /// step read the previous history from cache instead of paying for it again. Only
    /// the first-party endpoint gets the field: Anthropic-compatible gateways are free
    /// to reject it, and a rejected request would fail the run rather than run uncached.
    /// </summary>
    internal static bool UsesAutomaticPromptCache(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps
        && string.Equals(endpoint.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    private static JsonArray BuildTools(JsonArray? modelTools)
    {
        // An absent or empty tool list omits the key, matching the other two
        // protocol adapters: no tool is advertised, so there is nothing to
        // declare. Only a shape that cannot be projected stays fatal.
        if (modelTools is null || modelTools.Count == 0) return [];
        if (modelTools.Count > RuAgent.Protocol.AgentToolDefinitionCatalog.MaximumModelTools)
            throw new ModelProviderException("malformed_payload", false);
        var tools = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in modelTools.OfType<JsonObject>())
        {
            var name = ReadString(item["tool_id"]);
            var schema = item["input_schema"] as JsonObject;
            if (name is null || schema is null || !names.Add(name)) throw new ModelProviderException("malformed_payload", false);
            tools.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = ReadString(item["description"]) ?? string.Empty,
                ["input_schema"] = schema.DeepClone(),
            });
        }
        if (tools.Count != modelTools.Count) throw new ModelProviderException("malformed_payload", false);
        return tools;
    }

    private static ModelProviderException MapStatus(HttpStatusCode s, TimeSpan? retryAfter) => s switch
    {
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new("redirect_rejected", false),
        HttpStatusCode.Unauthorized => new("auth_failed", false),
        HttpStatusCode.Forbidden => new("forbidden", false),
        HttpStatusCode.TooManyRequests => new("rate_limited", true, retryAfter: retryAfter),
        // Gateways answer 404 both for unknown models and for models with no
        // serving capacity; either way the fix is picking another model.
        HttpStatusCode.NotFound => new("model_unavailable", false),

        // The provider answered, and the answer was "the account cannot pay". Kept out of the
        // generic 4xx bucket, whose copy sends the reader to /model and /key instead of to a
        // top-up.
        HttpStatusCode.PaymentRequired => new("provider_payment_required", false),
        (HttpStatusCode)529 => new("server_error", true, retryAfter: retryAfter),
        >= HttpStatusCode.InternalServerError => new("server_error", true, retryAfter: retryAfter),
        _ => new("http_error", false),
    };
}
