using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class OpenAiResponsesModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    public OpenAiResponsesModelProvider(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = BuildBody(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ModelProviderException("timeout", true); }
        catch (HttpRequestException ex) { throw new ModelProviderException("transport_error", true, ex); }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw MapStatus(response.StatusCode);
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ModelProviderException("timeout", true); }
            using (stream)
            {
                ModelDelta? terminal = null;
                await foreach (var evt in BoundedSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (terminal is not null) throw new ModelProviderException("malformed_payload", false);
                    if (evt.Event is null) throw new ModelProviderException("malformed_payload", false);
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
                        yield return new ModelDelta(t, [], false, null);
                        continue;
                    }
                    if (evt.Event == "response.completed")
                    {
                        var responseData = ReadObject(data["response"])
                            ?? throw new ModelProviderException("malformed_payload", false);
                        var model = ReadString(responseData["model"]);
                        if (model is null || model != request.Model) throw new ModelProviderException("malformed_payload", false);
                        var status = ReadString(responseData["status"]);
                        if (status is null) throw new ModelProviderException("malformed_payload", false);
                        var usage = ReadUsage(responseData["usage"]);
                        if (status == "completed")
                        {
                            terminal = new ModelDelta(null, [], true, "stop", usage);
                            continue;
                        }
                        if (status == "incomplete")
                        {
                            var incompleteDetails = ReadObject(responseData["incomplete_details"]);
                            var reason = ReadString(incompleteDetails?["reason"]);
                            if (reason == "max_output_tokens")
                            {
                                terminal = new ModelDelta(null, [], true, "length", usage);
                                continue;
                            }
                            throw new ModelProviderException("malformed_payload", false);
                        }
                        throw new ModelProviderException("malformed_payload", false);
                    }
                    if (evt.Event == "error" || type == "error")
                    {
                        var error = ReadObject(data["error"]);
                        var code = ReadString(error?["code"]) ?? ReadString(data["code"]) ?? "http_error";
                        throw new ModelProviderException(MapErrorCode(code), false);
                    }
                    if (evt.Event == "response.failed")
                    {
                        throw new ModelProviderException("http_error", false);
                    }
                    if (evt.Event is "response.created" or "response.in_progress" or "response.output_item.added" or "response.content_part.added" or "response.output_item.done" or "response.content_part.done"
                        or "response.reasoning.delta" or "response.reasoning.done" or "response.queued")
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
        foreach (var turn in request.Turns)
        {
            if (turn.Role is not ("user" or "assistant"))
                throw new ModelProviderException("malformed_payload", false);
            input.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

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
        else
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
        if (request.Settings["reasoning"] is JsonObject reasoning)
            body["reasoning"] = (JsonObject)reasoning.DeepClone();
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string MapErrorCode(string code) => code switch
    {
        "unauthorized" or "authentication_error" => "auth_failed",
        "permission_error" or "forbidden" => "forbidden",
        "rate_limit_exceeded" or "rate_limited" => "rate_limited",
        _ when code.Contains("rate_limit") => "rate_limited",
        _ => "http_error",
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

    private static ModelUsage? ReadUsage(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        if (node is not JsonObject usage)
            throw new ModelProviderException("malformed_payload", false);

        var input = ReadOptionalInt(usage["input_tokens"]);
        var output = ReadOptionalInt(usage["output_tokens"]);
        var total = ReadOptionalInt(usage["total_tokens"]);
        var cached = usage["input_tokens_details"] is JsonObject details
            ? ReadOptionalInt(details["cached_tokens"])
            : usage.ContainsKey("input_tokens_details")
                ? throw new ModelProviderException("malformed_payload", false)
                : null;
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
        throw new ModelProviderException("malformed_payload", false);
    }

    private static ModelProviderException MapStatus(HttpStatusCode s) => s switch
    {
        HttpStatusCode.Unauthorized => new("auth_failed", false),
        HttpStatusCode.Forbidden => new("forbidden", false),
        HttpStatusCode.TooManyRequests => new("rate_limited", true),
        >= HttpStatusCode.InternalServerError => new("server_error", true),
        _ => new("http_error", false),
    };
}
