using System.Text.Json;
using System.Text;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed class HarnessSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task WriteDurableAsync(
        HttpResponse response,
        HarnessSafeEventEnvelope envelope,
        CancellationToken cancellationToken,
        bool flush = true)
    {
        // Durable SSE frame: id: <sequence>\ndata: <json>\n\n
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (HarnessEventContractValidator.ContainsForbiddenField(json)) return Task.CompletedTask;
        return WriteFrameAsync(response, $"id: {envelope.Sequence}\ndata: {json}\n\n", cancellationToken, flush);
    }

    public static Task WriteFinalSnapshotAsync(
        HttpResponse response,
        string status,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { status });
        return WriteFrameAsync(response, $"event: final_snapshot\ndata: {json}\n\n", cancellationToken, true);
    }

    public static Task WriteTransientAsync(
        HttpResponse response,
        long localSequence,
        string eventType,
        object payload,
        CancellationToken cancellationToken,
        bool flush = true)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return WriteFrameAsync(response, $"event: transient\nid: t{localSequence}\ndata: {json}\n\n", cancellationToken, flush);
    }

    public static Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken cancellationToken) =>
        WriteFrameAsync(response, $": {comment}\n\n", cancellationToken, true);

    private static Task WriteFrameAsync(HttpResponse response, string frame, CancellationToken ct, bool flush)
    {
        ct.ThrowIfCancellationRequested();
        var writer = response.BodyWriter;
        var length = Encoding.UTF8.GetByteCount(frame);
        var written = Encoding.UTF8.GetBytes(frame.AsSpan(), writer.GetSpan(length));
        writer.Advance(written);
        return flush ? FlushAsync(response, ct) : Task.CompletedTask;
    }

    public static async Task FlushAsync(HttpResponse response, CancellationToken ct)
    {
        var result = await response.BodyWriter.FlushAsync(ct).ConfigureAwait(false);
        if (result.IsCanceled) throw new OperationCanceledException(ct);
    }
}
