using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Recognizes context rejection without retaining or exposing error bodies.</summary>
internal static class ModelContextOverflowError
{
    internal const string Code = "context_window_exceeded";
    private const int MaximumBytes = 16 * 1024;

    internal static bool IsCode(string? code) => code is "context_length_exceeded"
        or "context_window_exceeded" or "prompt_too_long" or "input_too_long";

    internal static bool IsError(JsonNode? error)
    {
        if (error is not JsonObject body) return false;
        return IsCode(Text(body["code"])) || IsCode(Text(body["type"]))
            || IsMessage(Text(body["message"]));
    }

    private static string? Text(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsMessage(string? message) => message is not null
        && (message.StartsWith("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("maximum context length", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("this model's maximum context length", StringComparison.OrdinalIgnoreCase));

    internal static async Task<bool> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        IsError(await ReadErrorAsync(response, ct).ConfigureAwait(false));

    /// <summary>
    /// A provider's hosted web search (ADR 0033) refused for this request: turned off for
    /// the organization or not offered for the model. Matched only for a request that
    /// declared the tool, from the error message the provider returns with a 400.
    /// </summary>
    internal static bool IsWebSearchUnavailable(JsonNode? error)
    {
        if (error is not JsonObject body || Text(body["message"]) is not { } message) return false;
        return message.Contains("web search", StringComparison.OrdinalIgnoreCase)
            || message.Contains("web_search", StringComparison.OrdinalIgnoreCase);
    }

    internal const string WebSearchUnavailableCode = "provider_web_search_unavailable";

    internal const string ToolCallingUnsupportedCode = "model_tools_unsupported";

    /// <summary>
    /// The model or server cannot take the tool declarations Ru sends with every
    /// request (ADR 0034). Matched only on the specific phrasings self-hosted
    /// servers use: Ollama ("does not support tools") and vLLM (auto tool choice
    /// not enabled). The error may be an object or a bare string.
    /// </summary>
    internal static bool IsToolCallingUnsupported(JsonNode? error)
    {
        var message = error is JsonObject body ? Text(body["message"]) : Text(error);
        if (message is null) return false;
        return message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tools are not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tool calling is not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("enable-auto-tool-choice", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bounded <c>error</c> object of a 400 or 413 body, or null.</summary>
    internal static async Task<JsonNode?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)) return null;
        if (response.Content.Headers.ContentLength > MaximumBytes) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[MaximumBytes + 1];
            var used = 0;
            while (used <= MaximumBytes)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(used), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    var root = JsonNode.Parse(bytes.AsSpan(0, used), documentOptions: new JsonDocumentOptions { MaxDepth = 8 });
                    return root?["error"];
                }
                used += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException or InvalidOperationException) { }
        ct.ThrowIfCancellationRequested();
        return null;
    }
}
