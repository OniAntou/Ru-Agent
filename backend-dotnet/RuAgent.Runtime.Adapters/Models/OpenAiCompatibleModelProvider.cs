using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleModelProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(BuildRequestBody(request), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(request.Credential))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException("timeout", retryable: true);
        }
        catch (HttpRequestException error)
        {
            throw new ModelProviderException("transport_error", retryable: true, error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapStatus(response.StatusCode);
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
                await foreach (var serverEvent in BoundedSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    sawData = true;
                    var payload = serverEvent.Data.Trim();
                    if (payload == "[DONE]")
                    {
                        if (!completionSeen)
                        {
                            yield return new ModelDelta(null, Array.Empty<ModelToolFragment>(), true, "stop");
                        }
                        yield break;
                    }

                    JsonObject data;
                    try
                    {
                        data = JsonNode.Parse(payload) as JsonObject
                            ?? throw new JsonException("payload is not an object");
                    }
                    catch (JsonException error)
                    {
                        throw new ModelProviderException("malformed_payload", retryable: false, error);
                    }

                    var usage = ReadUsage(data["usage"]);
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
                    var fragments = ReadToolFragments(delta);
                    var finishReason = ReadFinishReason(choice);
                    if (text is null && reasoning is null && fragments.Count == 0 && finishReason is null)
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
                    }
                    yield return new ModelDelta(text, fragments, completed, finishReason, usage, reasoning);
                }

                if (!sawData)
                    throw new ModelProviderException("malformed_payload", retryable: false);
            }
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
        foreach (var turn in request.Turns)
        {
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

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
        else
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
            if (property.Key is not ("model" or "messages" or "stream" or "stream_options" or "model_tools"))
            {
                body[property.Key] = property.Value?.DeepClone();
            }
        }

        // The Host/Worker selection contract becomes the provider's native
        // function-calling surface: the model calls capabilities directly and
        // the Host still owns policy, approval, and execution.
        var tools = BuildToolDeclarations(request.Settings);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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

    private static List<ModelToolFragment> ReadToolFragments(JsonObject delta)
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

        var reason = ReadOptionalString(choice["finish_reason"]);
        return reason is "stop" or "tool_calls" or "length"
            ? reason
            : throw new ModelProviderException("malformed_payload", retryable: false);
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

    private static ModelUsage? ReadUsage(JsonNode? node)
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
        if (input is null && output is null && total is null && cached is null)
            return null;
        return new ModelUsage(input, output, total, cached, null);
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0)
            return count;
        throw new ModelProviderException("malformed_payload", retryable: false);
    }

    private static ModelProviderException MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new ModelProviderException("auth_failed", retryable: false),
        HttpStatusCode.Forbidden => new ModelProviderException("forbidden", retryable: false),
        HttpStatusCode.TooManyRequests => new ModelProviderException("rate_limited", retryable: true),
        >= HttpStatusCode.InternalServerError => new ModelProviderException("server_error", retryable: true),
        _ => new ModelProviderException("http_error", retryable: false),
    };
}
