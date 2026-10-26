using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Adapters.Models;

public sealed partial class OpenAiResponsesModelProvider : IModelProvider, IModelRequestBodyProjector
{
    private readonly HttpClient _http;
    private readonly TimeSpan? _requestTimeout;
    public OpenAiResponsesModelProvider(HttpClient http)
        : this(http, requestTimeout: null)
    {
    }

    public OpenAiResponsesModelProvider(HttpClient http, TimeSpan? requestTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _requestTimeout = requestTimeout;
    }

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.PreparedBodyJson ?? BuildBody(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential);

        var response = await ModelHttpTransport.SendAsync(
            _http, message, cancellationToken, _requestTimeout).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode == HttpStatusCode.TooManyRequests
                    ? await ModelRateLimitError.ReadAsync(response, cancellationToken).ConfigureAwait(false)
                    : MapStatus(response.StatusCode, ModelRateLimitError.GetRetryAfter(response));
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ModelProviderException("timeout", true); }
            using (stream)
            {
                ModelDelta? terminal = null;
                var streamedText = new StringBuilder();
                var functionCalls = new Dictionary<int, FunctionCallStream>();
                await foreach (var evt in BoundedSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evt.Event is null) throw new ModelProviderException("malformed_payload", false);
                    if (evt.Event is not (
                        "response.output_text.delta" or "response.completed" or "response.incomplete" or "error"
                        or "response.failed" or "response.reasoning.delta" or "response.reasoning_summary_text.delta"
                        or "response.created" or "response.in_progress" or "response.output_item.added"
                        or "response.function_call_arguments.delta" or "response.function_call_arguments.done"
                        or "response.content_part.added" or "response.output_item.done" or "response.content_part.done"
                        or "response.reasoning.done" or "response.queued"))
                    {
                        // Forward compatibility: unknown future event names are
                        // ignored, like the Anthropic adapter does. Known event
                        // names keep their strict shape and ordering validation.
                        continue;
                    }
                    if (terminal is not null) throw new ModelProviderException("malformed_payload", false);
                    if (evt.Data.Length == 0) throw new ModelProviderException("malformed_payload", false);
                    JsonObject data;
                    try { data = JsonNode.Parse(evt.Data) as JsonObject ?? throw new JsonException(); }
                    catch (JsonException ex) { throw new ModelProviderException("malformed_payload", false, ex); }

                    var type = ReadString(data["type"]);
                    if (type is null || type != evt.Event)
                        throw new ModelProviderException("malformed_payload", false);
                    if (evt.Event == "response.output_text.delta")
                    {
                        var t = ReadString(data["delta"]);
                        if (t is null) throw new ModelProviderException("malformed_payload", false);
                        if (request.NativeToolCalling)
                        {
                            if (streamedText.Length + (long)t.Length > RuAgent.Harness.Core.Prompting.ResponsesAssistantMessages.MaximumTextCharacters)
                                throw new ModelProviderException("malformed_payload", false);
                            streamedText.Append(t);
                        }
                        yield return new ModelDelta(t, [], false, null);
                        continue;
                    }
                    if (evt.Event is "response.completed" or "response.incomplete")
                    {
                        var responseData = ReadObject(data["response"])
                            ?? throw new ModelProviderException("malformed_payload", false);
                        var model = ReadString(responseData["model"]);
                        if (model is null || model != request.Model) throw new ModelProviderException("malformed_payload", false);
                        var status = ReadString(responseData["status"]);
                        if (status is null) throw new ModelProviderException("malformed_payload", false);
                        var usage = ReadUsage(responseData["usage"], request.Model);
                        if (status == "incomplete")
                        {
                            var incompleteDetails = ReadObject(responseData["incomplete_details"]);
                            var reason = ReadString(incompleteDetails?["reason"]);
                            terminal = reason switch
                            {
                                "max_output_tokens" => new ModelDelta(null, [], true, "length", usage),
                                "content_filter" => new ModelDelta(null, [], true, "content_filter", usage),
                                _ => throw new ModelProviderException("malformed_payload", false),
                            };
                            continue;
                        }

                        if (evt.Event != "response.completed" || status != "completed")
                        {
                            throw new ModelProviderException("malformed_payload", false);
                        }

                        var output = ReadArray(responseData["output"]);
                        var calls = AssembleFunctionCalls(functionCalls, output);
                        var messages = ReadAssistantMessages(request, output);
                        if (messages is not null)
                        {
                            if (!messages.Text.StartsWith(streamedText.ToString(), StringComparison.Ordinal))
                                throw new ModelProviderException("malformed_payload", false);
                            for (var offset = streamedText.Length; offset < messages.Text.Length;)
                            {
                                var count = Math.Min(32 * 1024, messages.Text.Length - offset);
                                if (offset + count < messages.Text.Length && char.IsHighSurrogate(messages.Text[offset + count - 1])) count--;
                                yield return new ModelDelta(messages.Text.Substring(offset, count), [], false, null);
                                offset += count;
                            }
                        }
                        var continuation = BuildContinuation(request, output, calls.Count > 0);
                        terminal = new ModelDelta(null, calls, true, calls.Count > 0 ? "tool_calls" : "stop", usage,
                            providerContinuation: continuation, responsesMessages: messages);
                        continue;
                    }
                    if (evt.Event == "error" || type == "error")
                    {
                        var error = ReadObject(data["error"]);
                        var code = ReadString(error?["code"]) ?? ReadString(data["code"]) ?? "http_error";
                        throw MapError(code);
                    }
                    if (evt.Event == "response.failed")
                    {
                        var failed = ReadObject(data["response"]);
                        var error = ReadObject(failed?["error"]);
                        if (ModelRateLimitError.IsQuotaCode(ReadString(error?["code"])))
                            throw new ModelProviderException("quota_exhausted", false);
                        throw new ModelProviderException("http_error", false);
                    }
                    if (evt.Event is "response.reasoning.delta" or "response.reasoning_summary_text.delta")
                    {
                        // Reasoning summaries are the displayable part of the
                        // model's deliberation: surface them for the activity
                        // feed, never as visible chat text.
                        var summary = ReadString(data["delta"]);
                        if (summary is null) throw new ModelProviderException("malformed_payload", false);
                        if (summary.Length > 0)
                        {
                            yield return new ModelDelta(null, [], false, null, null, summary);
                        }
                        continue;
                    }
                    if (evt.Event == "response.output_item.added")
                    {
                        AddFunctionCall(functionCalls, data);
                        continue;
                    }
                    if (evt.Event is "response.function_call_arguments.delta" or "response.function_call_arguments.done")
                    {
                        AppendFunctionArguments(functionCalls, data, evt.Event.EndsWith(".done", StringComparison.Ordinal));
                        continue;
                    }
                    if (evt.Event is "response.created" or "response.in_progress" or "response.content_part.added" or "response.output_item.done" or "response.content_part.done"
                        or "response.reasoning.done" or "response.queued")
                    {
                        continue;
                    }
                    throw new ModelProviderException("malformed_payload", false);
                }
                if (terminal is not null)
                {
                    yield return terminal;
                    yield break;
                }
                throw new ModelProviderException("malformed_payload", false);
            }
        }
    }

    private static string BuildBody(ModelRequest request)
    {
        if (request.Images.Count > 0 && !ModelImage.IsVisionModel(request.Model))
            throw new ModelProviderException("vision_unsupported", false);

        var input = new JsonArray();
        var nativeToolsEnabled = request.Settings["model_tools"] is JsonArray;
        if (request.ResponsesInputItems.Count > 0)
        {
            foreach (var item in request.ResponsesInputItems) input.Add(item.DeepClone());
        }
        else foreach (var turn in request.Turns)
        {
            if (nativeToolsEnabled && turn.Role == "tool")
            {
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = turn.ToolCallId, ["output"] = ToolImageProjection.ResponsesOutput(turn) });
                continue;
            }
            if (nativeToolsEnabled && turn.Role == "assistant" && turn.ToolCalls.Count > 0)
            {
                if (turn.ResponsesMessages is { } toolMessages)
                    foreach (var message in toolMessages.Messages) input.Add(message.DeepClone());
                else if (!string.IsNullOrWhiteSpace(turn.Content)) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Content });
                foreach (var call in turn.ToolCalls)
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments.GetRawText() });
                continue;
            }
            if (turn.Role is not ("user" or "assistant"))
                throw new ModelProviderException("malformed_payload", false);
            if (nativeToolsEnabled && turn.ResponsesMessages is { } messages)
                foreach (var message in messages.Messages) input.Add(message.DeepClone());
            else input.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

        if (!string.IsNullOrEmpty(request.RuntimeContext))
            input.Add(new JsonObject { ["role"] = "user", ["content"] = request.RuntimeContext });

        if (request.Images.Count > 0)
        {
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "input_text", ["text"] = request.Prompt }
            };
            foreach (var img in request.Images)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = $"data:{img.MimeType};base64,{img.Base64Data}"
                });
            }
            input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        else if (!request.PromptInHistory)
        {
            input.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.SystemPrompt,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false,
        };
        if (request.MaximumOutputTokens is { } outputCap)
            body["max_output_tokens"] = outputCap;
        if (request.NativeToolCalling && request.Settings["model_tools"] is JsonArray modelTools)
        {
            var tools = new JsonArray();
            foreach (var toolNode in modelTools)
            {
                if (toolNode is not JsonObject tool
                    || ReadString(tool["tool_id"]) is not { } name
                    || tool["input_schema"] is not JsonObject schema)
                    throw new ModelProviderException("malformed_payload", false);
                tools.Add(new JsonObject { ["type"] = "function", ["name"] = name,
                    ["description"] = ReadString(tool["description"]) ?? string.Empty,
                    ["parameters"] = schema.DeepClone(), ["strict"] = false });
            }

            // An empty list omits the key rather than declaring no tools, the
            // same rule the other two protocol adapters follow.
            if (tools.Count > 0)
            {
                body["tools"] = tools;
            }
        }
        if (request.Settings["reasoning"] is JsonObject reasoning)
        {
            var outgoing = (JsonObject)reasoning.DeepClone();
            // Summaries are what the activity feed can display: without this the
            // API returns encrypted reasoning the client cannot show.
            if (outgoing["summary"] is null)
            {
                outgoing["summary"] = "auto";
            }
            body["reasoning"] = outgoing;
        }
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public string ProjectRequestBody(ModelRequest request) => BuildBody(request);

    private static RuAgent.Harness.Core.Prompting.ProviderContinuation? BuildContinuation(ModelRequest request, JsonArray? output, bool hasCalls)
    {
        if (!hasCalls || request.Settings["model_tools"] is not JsonArray || output is null) return null;
        var preserveMessages = output.OfType<JsonObject>().Any(item => ReadString(item["type"]) == "message" && item.ContainsKey("phase"));
        var continuationItems = output.OfType<JsonObject>()
            .Where(item => ReadString(item["type"]) is "reasoning" or "function_call"
                || preserveMessages && ReadString(item["type"]) == "message")
            .ToArray();
        if (continuationItems.Length == 0) return null;
        if (Encoding.UTF8.GetByteCount(new JsonArray(continuationItems.Select(item => item.DeepClone()).ToArray()).ToJsonString())
            > RuAgent.Harness.Core.Prompting.ProviderContinuation.MaximumBytes)
            throw new ModelProviderException("provider_continuation_too_large", false);
        try
        {
            return new RuAgent.Harness.Core.Prompting.ProviderContinuation("openai", "openai_responses", request.Model, 1, continuationItems);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new ModelProviderException("malformed_payload", false, error);
        }
    }

    private static void AddFunctionCall(Dictionary<int, FunctionCallStream> calls, JsonObject data)
    {
        var index = ReadIndex(data["output_index"]);
        if (data["item"] is not JsonObject item || ReadString(item["type"]) != "function_call") return;
        var itemId = ReadString(item["id"]);
        var callId = ReadString(item["call_id"]);
        var name = ReadString(item["name"]);
        if (itemId is null || callId is null || name is null || calls.ContainsKey(index)
            || calls.Values.Any(call => call.ItemId == itemId || call.CallId == callId))
            throw new ModelProviderException("malformed_payload", false);
        calls.Add(index, new FunctionCallStream(itemId, callId, name));
    }

    private static void AppendFunctionArguments(Dictionary<int, FunctionCallStream> calls, JsonObject data, bool isFinal)
    {
        var index = ReadIndex(data["output_index"]);
        var itemId = ReadString(data["item_id"]);
        var delta = ReadString(data[isFinal ? "arguments" : "delta"]);
        if (!calls.TryGetValue(index, out var call) || call.ItemId != itemId || delta is null || call.IsFinal)
            throw new ModelProviderException("malformed_payload", false);
        if (!isFinal)
        {
            call.Arguments.Append(delta);
        }
        else if (call.Arguments.Length == 0)
        {
            // A gateway may normalise the stream and send only the completed
            // `.done` payload. The done value is then the source of truth for
            // the arguments rather than a redundant echo that must match
            // deltas that never arrived.
            call.Arguments.Append(delta);
        }
        else if (call.Arguments.ToString() != delta)
        {
            // With deltas present, the done payload must agree with them: a
            // disagreement is a broken stream, not a recoverable shape.
            throw new ModelProviderException("malformed_payload", false);
        }

        if (call.Arguments.Length > 64 * 1024) throw new ModelProviderException("response_too_large", false);
        if (isFinal) call.IsFinal = true;
    }

    private static IReadOnlyList<ModelToolFragment> AssembleFunctionCalls(Dictionary<int, FunctionCallStream> streams, JsonArray? output)
    {
        var outputCalls = output?.OfType<JsonObject>().Where(item => ReadString(item["type"]) == "function_call").ToArray() ?? [];
        if (outputCalls.Length != streams.Count || outputCalls.Length > 16) throw new ModelProviderException("malformed_payload", false);
        var result = new List<ModelToolFragment>(outputCalls.Length);
        foreach (var item in outputCalls)
        {
            var id = ReadString(item["id"]);
            var callId = ReadString(item["call_id"]);
            var name = ReadString(item["name"]);
            var arguments = ReadString(item["arguments"]);
            var stream = streams.Values.SingleOrDefault(value => value.ItemId == id);
            if (stream is null || !stream.IsFinal || stream.CallId != callId || stream.Name != name || stream.Arguments.ToString() != arguments || callId is null || name is null || arguments is null)
                throw new ModelProviderException("malformed_payload", false);
            result.Add(new ModelToolFragment(callId, name, arguments));
        }
        return result;
    }

    private static int ReadIndex(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0 ? index : throw new ModelProviderException("malformed_payload", false);
    private static JsonArray? ReadArray(JsonNode? node) => node is null || node.GetValueKind() == JsonValueKind.Null ? null : node as JsonArray ?? throw new ModelProviderException("malformed_payload", false);

    private sealed class FunctionCallStream(string itemId, string callId, string name)
    {
        public string ItemId { get; } = itemId;
        public string CallId { get; } = callId;
        public string Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
        public bool IsFinal { get; set; }
    }

    /// <summary>
    /// Stream-level error codes carry their own retryability, the same way the
    /// HTTP status mapping does: a rate limit or a server fault reached the
    /// provider and is transient, so it must not fail the run closed on the
    /// first occurrence.
    /// </summary>
    private static ModelProviderException MapError(string code) => code switch
    {
        _ when ModelRateLimitError.IsQuotaCode(code) => new("quota_exhausted", false),
        "unauthorized" or "authentication_error" => new("auth_failed", false),
        "permission_error" or "forbidden" => new("forbidden", false),
        "rate_limit_exceeded" or "rate_limited" => new("rate_limited", true),
        _ when code.Contains("rate_limit") => new("rate_limited", true),
        "server_error" or "overloaded" or "overloaded_error" => new("server_error", true),
        "402" or "payment_required" => new("provider_payment_required", false),
        _ => new("http_error", false),
    };

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static JsonObject? ReadObject(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject value) return value;
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
        var total = ReadOptionalInt(usage["total_tokens"]);
        var detailsNode = usage["input_tokens_details"];
        var cached = detailsNode is null || detailsNode.GetValueKind() == JsonValueKind.Null
            ? null
            : detailsNode is JsonObject details
                ? ReadOptionalInt(details["cached_tokens"])
                : throw new ModelProviderException("malformed_payload", false);
        if (input is null && output is null && total is null && cached is null)
            return null;
        if (input is { } prompt && cached is { } read && read > prompt)
            throw new ModelProviderException("malformed_payload", retryable: false);
        return new ModelUsage(input, output, total, cached, null, model, inputTokensIncludeCache: true);
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0)
            return count;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static ModelProviderException MapStatus(HttpStatusCode s, TimeSpan? retryAfter) => s switch
    {
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new("redirect_rejected", false),
        HttpStatusCode.Unauthorized => new("auth_failed", false),
        HttpStatusCode.Forbidden => new("forbidden", false),
        HttpStatusCode.TooManyRequests => new("rate_limited", true),
        // Gateways answer 404 both for unknown models and for models with no
        // serving capacity; either way the fix is picking another model.
        HttpStatusCode.NotFound => new("model_unavailable", false),
        HttpStatusCode.PaymentRequired => new("provider_payment_required", false),
        >= HttpStatusCode.InternalServerError => new("server_error", true, retryAfter: retryAfter),
        _ => new("http_error", false),
    };
}
