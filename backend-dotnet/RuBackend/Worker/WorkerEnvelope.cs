using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuBackend.Worker;

public sealed record WorkerEnvelope(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("seq")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Seq,
    [property: JsonPropertyName("payload")] JsonObject Payload,
    [property: JsonPropertyName("correlation_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CorrelationId = null)
{
    public WorkerEnvelope(
        int v,
        string id,
        string type,
        long? seq,
        JsonObject payload)
        : this(v, id, type, seq, payload, null)
    {
    }
}

public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string code)
        : this(code, retryable: false)
    {
    }

    public WorkerProtocolException(string code, bool retryable)
        : base(code)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    /// <summary>Worker-reported transient signal (e.g. rate limits). Preserved
    /// from the error envelope so the step driver can retry bounded failures
    /// instead of failing the run.</summary>
    public bool Retryable { get; }
}

public static class WorkerEnvelopeCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static WorkerEnvelope Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                throw new WorkerProtocolException("invalid_envelope");
            }

            var version = ReadInt32(root["v"], "invalid_version");
            var id = ReadString(root["id"], "invalid_id");
            var type = ReadString(root["type"], "unknown_type");
            var sequence = ReadOptionalSequence(root["seq"]);
            if (root["payload"] is not JsonObject payload)
            {
                throw new WorkerProtocolException("invalid_payload");
            }

            string? correlationId = null;
            if (root.TryGetPropertyValue("correlation_id", out var cNode) && cNode is JsonValue cVal && cVal.TryGetValue<string>(out var cStr))
            {
                correlationId = string.IsNullOrWhiteSpace(cStr) ? null : cStr.Trim();
            }

            var envelope = new WorkerEnvelope(
                version,
                id,
                type,
                sequence,
                payload.DeepClone().AsObject(),
                correlationId);
            Validate(envelope);
            return envelope;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new WorkerProtocolException("invalid_json");
        }
    }

    public static string Serialize(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(envelope);
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static void Validate(WorkerEnvelope envelope)
    {
        if (envelope.V != 1)
        {
            throw new WorkerProtocolException("unsupported_version");
        }

        if (string.IsNullOrWhiteSpace(envelope.Id) || envelope.Id.Length > 128)
        {
            throw new WorkerProtocolException("invalid_id");
        }

        if (!WorkerMessageTypes.Known.Contains(envelope.Type))
        {
            throw new WorkerProtocolException("unknown_type");
        }

        if (envelope.Seq < 0)
        {
            throw new WorkerProtocolException("invalid_sequence");
        }

        if (WorkerMessageTypes.OrderedResponses.Contains(envelope.Type) && envelope.Seq is null)
        {
            throw new WorkerProtocolException("missing_sequence");
        }

        if (envelope.Payload is null)
        {
            throw new WorkerProtocolException("invalid_payload");
        }

    }

    private static int ReadInt32(JsonNode? node, string code)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException(code);
    }

    private static string ReadString(JsonNode? node, string code)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException(code);
    }

    private static long? ReadOptionalSequence(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        throw new WorkerProtocolException("invalid_sequence");
    }
}

public static class WorkerDiagnostics
{
    public static string Summarize(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Id)))
            .ToLowerInvariant()[..12];
        return $"type={envelope.Type} id_hash={hash} seq={envelope.Seq?.ToString() ?? "none"}";
    }
}
