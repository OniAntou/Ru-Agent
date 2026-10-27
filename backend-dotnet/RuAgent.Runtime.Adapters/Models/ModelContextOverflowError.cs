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

    internal static async Task<bool> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)) return false;
        if (response.Content.Headers.ContentLength > MaximumBytes) return false;
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
                    return IsError(root?["error"]);
                }
                used += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException or InvalidOperationException) { }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}
