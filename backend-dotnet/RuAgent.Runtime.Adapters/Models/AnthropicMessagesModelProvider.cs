using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class AnthropicMessagesModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    public AnthropicMessagesModelProvider(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumOutputTokens is null or <= 0 or > 8192) throw new ModelProviderException("malformed_payload", false);
        var body = BuildBody(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", request.Credential);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

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
                var phase = Phase.ExpectMessageStart;
                string? stopReason = null;
                int? blockIndex = null;
                string blockType = string.Empty;

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
                            if (idx is null) throw new ModelProviderException("malformed_payload", false);
                            if (blockIndex is not null) throw new ModelProviderException("malformed_payload", false);
                            var block = data["content_block"] as JsonObject ?? throw new ModelProviderException("malformed_payload", false);
                            var t = ReadString(block["type"]) ?? throw new ModelProviderException("malformed_payload", false);
                            if (t == "fallback" || t == "tool_use") throw new ModelProviderException("malformed_payload", false);
                            if (t != "text" && t != "thinking") throw new ModelProviderException("malformed_payload", false);
                            blockIndex = idx;
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
                                yield return new ModelDelta(text, [], false, null);
                            }
                            else if (deltaType is "thinking_delta" or "signature_delta")
                            {
                                if (blockType != "thinking") throw new ModelProviderException("malformed_payload", false);
                            }
                            else throw new ModelProviderException("malformed_payload", false);
                            break;
                        }
                        case "content_block_stop":
                        {
                            if (phase != Phase.InBlock || blockIndex is null) throw new ModelProviderException("malformed_payload", false);
                            var idx = ReadInt(data["index"]);
                            if (idx != blockIndex) throw new ModelProviderException("malformed_payload", false);
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
                            if (reason == "tool_use") throw new ModelProviderException("malformed_payload", false);
                            if (reason is not ("end_turn" or "stop_sequence" or "max_tokens")) throw new ModelProviderException("malformed_payload", false);
                            stopReason = reason == "max_tokens" ? "length" : "stop";
                            phase = Phase.GotDelta;
                            break;
                        }
                        case "message_stop":
                        {
                            if (phase != Phase.GotDelta) throw new ModelProviderException("malformed_payload", false);
                            yield return new ModelDelta(null, [], true, stopReason ?? "stop");
                            yield break;
                        }
                        case "error":
                        {
                            var error = ReadObject(data["error"]);
                            var errType = ReadString(error?["type"]) ?? string.Empty;
                            if (errType == "overloaded_error") throw new ModelProviderException("server_error", true);
                            throw new ModelProviderException("http_error", false);
                        }
                        default: continue;
                    }
                }
                throw new ModelProviderException("malformed_payload", false);
            }
        }
    }

    private enum Phase { ExpectMessageStart, InMessage, InBlock, GotDelta }

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
        foreach (var turn in request.Turns)
        {
            if (turn.Role is not ("user" or "assistant"))
                throw new ModelProviderException("malformed_payload", false);
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

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
        else
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
        if (request.Settings["thinking"] is JsonObject thinking) body["thinking"] = (JsonObject)thinking.DeepClone();
        if (request.Settings["output_config"] is JsonObject oc) body["output_config"] = (JsonObject)oc.DeepClone();
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static ModelProviderException MapStatus(HttpStatusCode s) => s switch
    {
        HttpStatusCode.Unauthorized => new("auth_failed", false),
        HttpStatusCode.Forbidden => new("forbidden", false),
        HttpStatusCode.TooManyRequests => new("rate_limited", true),
        (HttpStatusCode)529 => new("server_error", true),
        >= HttpStatusCode.InternalServerError => new("server_error", true),
        _ => new("http_error", false),
    };
}
