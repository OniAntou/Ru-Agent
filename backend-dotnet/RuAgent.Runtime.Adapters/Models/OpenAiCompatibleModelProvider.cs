using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private const int MaxResponseBytes = 5 * 1024 * 1024;
    private const int MaxLineBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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
            using (var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false))
            {
                long responseBytes = 0;
                var sawData = false;
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelProviderException("timeout", retryable: true);
                    }
                    catch (DecoderFallbackException error)
                    {
                        throw new ModelProviderException("malformed_payload", retryable: false, error);
                    }

                    if (line is null)
                    {
                        break;
                    }

                    var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                    responseBytes += lineBytes;
                    if (lineBytes > MaxLineBytes || responseBytes > MaxResponseBytes)
                    {
                        throw new ModelProviderException("response_too_large", retryable: false);
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sawData = true;
                    var payload = line[5..].Trim();
                    if (payload == "[DONE]")
                    {
                        yield return new ModelDelta(null, Array.Empty<ModelToolFragment>(), true, "stop");
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

                    if (data["choices"] is not JsonArray choices || choices.Count == 0
                        || choices[0] is not JsonObject choice
                        || choice["delta"] is not JsonObject delta)
                    {
                        throw new ModelProviderException("malformed_payload", retryable: false);
                    }

                    var text = ReadText(delta);
                    var fragments = ReadToolFragments(delta);
                    var finishReason = ReadFinishReason(choice);
                    if (text is null && fragments.Count == 0 && finishReason is null)
                    {
                        continue;
                    }

                    var completed = finishReason is not null;
                    yield return new ModelDelta(text, fragments, completed, finishReason);
                    if (completed)
                    {
                        yield break;
                    }
                }

                if (!sawData)
                {
                    throw new ModelProviderException("malformed_payload", retryable: false);
                }
            }
        }
    }

    private static string BuildRequestBody(ModelRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var turn in request.Turns)
        {
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        foreach (var property in request.Settings)
        {
            // model_tools is a private Host/Worker selection contract. The
            // managed Agent asks the model for a single JSON selection through
            // its composed prompt; provider-native function calling is not part
            // of this adapter contract.
            if (property.Key is not ("model" or "messages" or "stream" or "model_tools"))
            {
                body[property.Key] = property.Value?.DeepClone();
            }
        }

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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

    private static ModelProviderException MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new ModelProviderException("auth_failed", retryable: false),
        HttpStatusCode.Forbidden => new ModelProviderException("forbidden", retryable: false),
        HttpStatusCode.TooManyRequests => new ModelProviderException("rate_limited", retryable: true),
        >= HttpStatusCode.InternalServerError => new ModelProviderException("server_error", retryable: true),
        _ => new ModelProviderException("http_error", retryable: false),
    };
}
