using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class OpenAiCompatibleModelProvider : IModelProvider, IModelRequestBodyProjector
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan? _requestTimeout;

    public OpenAiCompatibleModelProvider(HttpClient httpClient)
        : this(httpClient, requestTimeout: null)
    {
    }

    public OpenAiCompatibleModelProvider(HttpClient httpClient, TimeSpan? requestTimeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout;
    }

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(request.PreparedBodyJson ?? BuildRequestBody(request), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(request.Credential))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential);
        }

        var response = await ModelHttpTransport.SendAsync(
            _httpClient, message, cancellationToken, _requestTimeout).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode == HttpStatusCode.TooManyRequests
                    ? await ModelRateLimitError.ReadAsync(response, cancellationToken).ConfigureAwait(false)
                    : MapStatus(response.StatusCode, ModelRateLimitError.GetRetryAfter(response));
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderException("timeout", retryable: true);
            }

            using (stream)
            {
                var sawData = false;
                // chat_completions with include_usage sends the usage report in a
                // final `choices: []` chunk that arrives *after* the finish_reason
                // chunk. Ending the enumeration on finish_reason dropped that tail
                // for every run; instead, completion is remembered and only the
                // usage tail is still consumed afterwards.
                var completionSeen = false;
                var indexedCalls = new ChatToolCallAssembly();
                // A chunk cut mid-string cannot parse on its own. Sloppy emitters also
                // split escape sequences across chunks, so a lone trailing backslash is
                // a continuation, never content to repair yet.
                const int MaxCarriedChunkChars = 64 * 1024;
                string? carriedPrefix = null;
                await foreach (var serverEvent in BoundedSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    sawData = true;
                    var payload = serverEvent.Data.Trim();
                    if (payload == "[DONE]")
                    {
                        if (carriedPrefix is not null)
                        {
                            // The truncated chunk never completed: accepting the stream
                            // here would deliver a half-written answer the caller has no
                            // way to recognize as incomplete.
                            throw new ModelProviderException("malformed_payload", retryable: false);
                        }

                        if (!completionSeen)
                        {
                            yield return new ModelDelta(null, indexedCalls.Complete(), true, "stop");
                        }
                        yield break;
                    }

                    var candidate = carriedPrefix is null ? payload : carriedPrefix + payload;
                    carriedPrefix = null;
                    var data = TryParseChunk(candidate);
                    if (data is null)
                    {
                        if (JsonEscapeRepair.LooksTruncated(candidate)
                            && candidate.Length <= MaxCarriedChunkChars)
                        {
                            carriedPrefix = candidate;
                            continue;
                        }

                        if (JsonEscapeRepair.TryRepairInvalidEscapes(candidate, out var repaired)
                            && TryParseChunk(repaired) is { } fixedChunk)
                        {
                            data = fixedChunk;
                        }
                        else
                        {
                            throw new ModelProviderException("malformed_payload", retryable: false);
                        }
                    }

                    if (data["error"] is JsonObject providerError
                        && providerError["code"] is JsonValue errorCode
                        && errorCode.TryGetValue<string>(out var code)
                        && ModelRateLimitError.IsQuotaCode(code))
                        throw new ModelProviderException("quota_exhausted", retryable: false);

                    var usage = ReadUsage(data["usage"], request.Model);
                    if (data["choices"] is not JsonArray choices)
                    {
                        throw new ModelProviderException("malformed_payload", retryable: false);
                    }

                    if (choices.Count == 0)
                    {
                        if (usage is not null)
                        {
                            yield return new ModelDelta(null, Array.Empty<ModelToolFragment>(), false, null, usage);
                        }
                        continue;
                    }

                    if (choices[0] is not JsonObject choice || choice["delta"] is not JsonObject delta)
                    {
                        throw new ModelProviderException("malformed_payload", retryable: false);
                    }

                    var text = ReadText(delta);
                    // Gateways disagree on the field name: OpenRouter-style streams
                    // use reasoning_content while others use reasoning. Either one
                    // is provider thinking, never visible chat text. The fallback
                    // stays lenient: a structured reasoning block is skipped, not
                    // a protocol failure.
                    var reasoning = ReadOptionalString(delta["reasoning_content"])
                        ?? TryReadString(delta["reasoning"]);
                    var fragments = ReadToolFragments(delta, indexedCalls);
                    var finishReason = ReadFinishReason(choice);
                    if (text is null && reasoning is null && fragments.Count == 0 && finishReason is null && usage is null)
                    {
                        continue;
                    }

                    if (completionSeen)
                    {
                        // Only the usage tail may follow the finish_reason chunk;
                        // anything else from this provider is not forwarded.
                        if (usage is not null)
                        {
                            yield return new ModelDelta(
                                null,
                                Array.Empty<ModelToolFragment>(),
                                false,
                                null,
                                usage);
                        }
                        continue;
                    }

                    var completed = finishReason is not null;
                    if (completed)
                    {
                        completionSeen = true;
                        fragments.AddRange(indexedCalls.Complete());
                    }
                    yield return new ModelDelta(text, fragments, completed, finishReason, usage, reasoning);
                }

                if (!sawData)
                    throw new ModelProviderException("malformed_payload", retryable: false);
                if (!completionSeen)
                {
                    // The stream ended without the terminal marker this protocol uses:
                    // either a chunk carrying finish_reason or `data: [DONE]`. A cut
                    // connection looks exactly like this, and accepting it would deliver
                    // a half-written answer that the caller has no way to recognize as
                    // incomplete. The Anthropic and Responses adapters already fail closed
                    // here; this one used to accept "some data arrived" as completion.
                    throw new ModelProviderException("malformed_payload", retryable: false);
                }
            }
        }
    }

    public string ProjectRequestBody(ModelRequest request) => BuildRequestBody(request);

    /// <summary>
    /// Parses one SSE data chunk, returning null instead of throwing when it is not a
    /// JSON object. Callers decide whether to buffer (truncated), repair (invalid
    /// escapes) or reject, so every malformed shape keeps flowing through one place.
    /// </summary>
    private static JsonObject? TryParseChunk(string candidate)
    {
        try
        {
            return JsonNode.Parse(candidate) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildRequestBody(ModelRequest request)
    {
        if (request.Images.Count > 0 && !ModelImage.IsVisionModel(request.Model))
            throw new ModelProviderException("vision_unsupported", false);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        var pendingToolImages = new List<ModelTurn>();
        foreach (var turn in request.Turns)
        {
            if (turn.Role != "tool")
            {
                FlushToolImages(messages, pendingToolImages);
            }
            else if (turn.Images.Count > 0)
            {
                pendingToolImages.Add(turn);
            }

            messages.Add(SerializeTurn(turn, request.ReplayChatReasoning));
        }

        FlushToolImages(messages, pendingToolImages);
        if (!string.IsNullOrEmpty(request.RuntimeContext))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.RuntimeContext });

        if (request.Images.Count > 0)
        {
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = request.Prompt }
            };
            foreach (var img in request.Images)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{img.MimeType};base64,{img.Base64Data}"
                    }
                });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        else if (!request.PromptInHistory)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (request.MaximumOutputTokens is { } outputCap)
            body[request.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = outputCap;
        // OpenRouter-style gateways strip reasoning blocks from the response
        // unless the request asks for them. Other agents send this flag, which
        // is why they see thinking where Ru saw a bare answer. Only sent when
        // reasoning was actually requested through Host-approved settings, so
        // endpoints that never advertise reasoning keep their exact body.
        if (!body.ContainsKey("include_reasoning") && ReasoningRequested(request.Settings))
        {
            body["include_reasoning"] = true;
        }
        foreach (var property in request.Settings)
        {
            if (property.Key is not ("model" or "messages" or "stream" or "stream_options" or "model_tools"
                or "max_tokens" or "max_completion_tokens" or "max_output_tokens"))
            {
                body[property.Key] = property.Value?.DeepClone();
            }
        }

        // The Host/model-step selection contract becomes the provider's native
        // function-calling surface: the model calls capabilities directly and
        // the Host still owns policy, approval, and execution.
        var tools = request.NativeToolCalling ? BuildToolDeclarations(request.Settings) : new JsonArray();
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Chat Completions accepts only text in <c>tool</c> messages, and nothing may come
    /// between the tool messages of one batch. Images a batch returned therefore follow
    /// its last tool message as one user message naming each call (ADR 0026).
    /// </summary>
    private static void FlushToolImages(JsonArray messages, List<ModelTurn> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var content = new JsonArray();
        foreach (var turn in pending)
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Image returned by tool call {turn.ToolCallId}:",
            });
            foreach (var image in turn.Images)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = image.DataUrl },
                });
            }
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        pending.Clear();
    }

    private static JsonObject SerializeTurn(ModelTurn turn, bool replayReasoning)
    {
        var message = new JsonObject
        {
            ["role"] = turn.Role,
            ["content"] = turn.Content,
        };
        if (replayReasoning && turn.ReasoningContent is not null)
            message["reasoning_content"] = turn.ReasoningContent;
        if (turn.Role == "assistant" && turn.ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in turn.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.GetRawText(),
                    },
                });
            }
            message["tool_calls"] = calls;
        }
        else if (turn.Role == "tool")
        {
            message["tool_call_id"] = turn.ToolCallId;
        }

        return message;
    }

    private static JsonArray BuildToolDeclarations(JsonObject settings)
    {
        var tools = new JsonArray();
        if (settings["model_tools"] is not JsonArray modelTools)
        {
            return tools;
        }

        foreach (var node in modelTools)
        {
            if (node is not JsonObject tool
                || tool["tool_id"]?.GetValue<string>() is not { Length: > 0 } toolId
                || tool["input_schema"] is not JsonObject schema)
            {
                throw new ModelProviderException("malformed_payload", retryable: false);
            }

            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolId,
                    ["description"] = ReadOptionalString(tool["description"]) ?? string.Empty,
                    ["parameters"] = schema.DeepClone(),
                },
            });
        }

        return tools;
    }

    private static string? ReadText(JsonObject delta)
    {
        return ReadOptionalString(delta["content"]);
    }

    private static List<ModelToolFragment> ReadToolFragments(JsonObject delta, ChatToolCallAssembly indexedCalls)
    {
        var fragments = new List<ModelToolFragment>();
        if (delta["tool_calls"] is not JsonArray calls)
        {
            return fragments;
        }

        if (calls.Count > 16)
        {
            throw new ModelProviderException("malformed_payload", retryable: false);
        }

        foreach (var node in calls)
        {
            if (node is not JsonObject call)
            {
                throw new ModelProviderException("malformed_payload", retryable: false);
            }

            var providerCallId = ReadOptionalString(call["id"]);
            if (call["function"] is not JsonObject function)
            {
                throw new ModelProviderException("malformed_payload", retryable: false);
            }

            var name = ReadOptionalString(function["name"]) ?? string.Empty;
            var arguments = ReadOptionalString(function["arguments"]) ?? string.Empty;
            if (call.ContainsKey("index"))
            {
                if (call["index"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out var index))
                    throw new ModelProviderException("malformed_payload", retryable: false);
                indexedCalls.Append(index, providerCallId, name, arguments);
                continue;
            }
            indexedCalls.ObserveUnindexed();
            try
            {
                fragments.Add(new ModelToolFragment(providerCallId, name, arguments));
            }
            catch (ArgumentException error)
            {
                throw new ModelProviderException("malformed_payload", retryable: false, error);
            }
        }

        return fragments;
    }

    private static string? ReadFinishReason(JsonObject choice)
    {
        if (choice["finish_reason"] is null || choice["finish_reason"]?.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var reason = TryReadString(choice["finish_reason"]);
        if (reason is null)
        {
            // Present but unreadable -- a number, an object, whatever this
            // gateway sent. It is the same species as an unknown marker: the
            // step still ends when the stream closes, and a finished step with
            // usable content is worth more than a failed conversation. Throwing
            // here is what turned a preview endpoint's odd vocabulary into
            // protocol_failure on the very first step, with no tool call and
            // nothing for the user to fix.
            return null;
        }

        return reason switch
        {
            "stop" or "tool_calls" or "length" => reason,
            // Gateways pass provider-native stop markers (end_turn, complete,
            // content_filter, ...) through their OpenAI-compatible surface. An
            // unknown marker still ends the turn: failing the run on vocabulary
            // turned answers into protocol_failure after tools already ran.
            { Length: > 0 } => "stop",
            _ => null,
        };
    }

    private static string? ReadOptionalString(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new ModelProviderException("malformed_payload", retryable: false);
    }

    private static string? TryReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReasoningRequested(JsonObject settings) =>
        settings.ContainsKey("reasoning_effort") || settings.ContainsKey("reasoning");

    private static ModelUsage? ReadUsage(JsonNode? node, string? model)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is not JsonObject usage)
            throw new ModelProviderException("malformed_payload", retryable: false);

        var input = ReadOptionalInt(usage["prompt_tokens"]);
        var output = ReadOptionalInt(usage["completion_tokens"]);
        var total = ReadOptionalInt(usage["total_tokens"]);
        var detailsNode = usage["prompt_tokens_details"];
        var cached = detailsNode is null || detailsNode.GetValueKind() == JsonValueKind.Null
            ? null
            : detailsNode is JsonObject details
                ? ReadOptionalInt(details["cached_tokens"])
                : throw new ModelProviderException("malformed_payload", retryable: false);
        var hit = ReadOptionalInt(usage["prompt_cache_hit_tokens"]);
        var miss = ReadOptionalInt(usage["prompt_cache_miss_tokens"]);
        if (hit is not null && cached is not null && hit != cached)
            throw new ModelProviderException("malformed_payload", retryable: false);
        cached ??= hit;
        if (hit is { } h && miss is { } m)
        {
            long sum = (long)h + m;
            if (sum > int.MaxValue || (input is { } reported && reported != sum))
                throw new ModelProviderException("malformed_payload", retryable: false);
            input ??= (int)sum;
        }
        if (input is { } prompt && cached is { } read && read > prompt)
            throw new ModelProviderException("malformed_payload", retryable: false);
        if (input is null && output is null && total is null && cached is null)
            return null;
        return new ModelUsage(input, output, total, cached, null, model, inputTokensIncludeCache: true);
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0)
            return count;
        throw new ModelProviderException("malformed_payload", retryable: false);
    }

    private static ModelProviderException MapStatus(HttpStatusCode statusCode, TimeSpan? retryAfter) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new ModelProviderException("auth_failed", retryable: false),
        HttpStatusCode.Forbidden => new ModelProviderException("forbidden", retryable: false),
        HttpStatusCode.TooManyRequests => new ModelProviderException("rate_limited", retryable: true),
        // Gateways answer 404 both for unknown models and for models with no
        // serving capacity; either way the fix is picking another model.
        HttpStatusCode.NotFound => new ModelProviderException("model_unavailable", retryable: false),
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new ModelProviderException("redirect_rejected", retryable: false),

        // The provider answered, and the answer was "the account cannot pay". Folding this into
        // the generic 4xx bucket told the reader to check the model and the key, neither of which
        // is wrong, so an empty balance looked like a configuration mistake.
        HttpStatusCode.PaymentRequired => new ModelProviderException("provider_payment_required", retryable: false),
        >= HttpStatusCode.InternalServerError => new ModelProviderException("server_error", retryable: true, retryAfter: retryAfter),
        _ => new ModelProviderException("http_error", retryable: false),
    };
}
