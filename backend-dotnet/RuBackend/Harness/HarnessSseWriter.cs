using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed class HarnessSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteDurableAsync(
        HttpResponse response,
        HarnessSafeEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        // Durable SSE frame: id: <sequence>\ndata: <json>\n\n
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await response.WriteAsync($"id: {envelope.Sequence}\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteFinalSnapshotAsync(
        HttpResponse response,
        string status,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { status });
        await response.WriteAsync($"event: final_snapshot\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteTransientAsync(
        HttpResponse response,
        long localSequence,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"event: transient\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"id: t{localSequence}\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken cancellationToken)
    {
        await response.WriteAsync($": {comment}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
