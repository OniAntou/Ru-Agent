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
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
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

        switch (envelope.Type)
        {
            case WorkerMessageTypes.ChatToolRequest:
                ValidateToolRequest(envelope.Payload);
                break;
            case WorkerMessageTypes.ChatToolResult:
                ValidateToolResult(envelope.Payload);
                break;
            case WorkerMessageTypes.ChatToolCancel:
                ValidateToolContext(envelope.Payload, "invalid_tool_cancel");
                ValidateToolRequestId(envelope.Payload, "invalid_tool_cancel");
                break;
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

    private static void ValidateToolRequest(JsonObject payload)
    {
        ValidateToolContext(payload, "invalid_tool_request");
        ValidateToolRequestId(payload, "invalid_tool_request");
        ValidateString(payload["tool_id"], 128, "invalid_tool_request");
        ValidateString(payload["tool_version"], 64, "invalid_tool_request");
        if (payload["arguments"] is not JsonObject)
        {
            throw new WorkerProtocolException("invalid_tool_request");
        }

        var deadline = ReadInt32(payload["deadline_ms"], "invalid_tool_request");
        if (deadline is < 1 or > 300_000)
        {
            throw new WorkerProtocolException("invalid_tool_request");
        }
    }

    private static void ValidateToolResult(JsonObject payload)
    {
        ValidateToolRequestId(payload, "invalid_tool_result");
        var status = ReadString(payload["status"], "invalid_tool_result");
        if (!new HashSet<string>(["succeeded", "failed", "denied", "cancelled", "timed_out"], StringComparer.Ordinal).Contains(status)
            || payload["output"] is not JsonObject
            || payload["safe_summary"] is not JsonValue safeSummary
            || !safeSummary.TryGetValue<string>(out var summary)
            || summary.Length > 4096)
        {
            throw new WorkerProtocolException("invalid_tool_result");
        }
    }

    private static void ValidateToolContext(JsonObject payload, string code)
    {
        ValidateString(payload["session_id"], 128, code);
        ValidateString(payload["correlation_id"], 128, code);
    }

    private static void ValidateToolRequestId(JsonObject payload, string code) =>
        ValidateString(payload["request_id"], 128, code);

    private static void ValidateString(JsonNode? node, int maxLength, string code)
    {
        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maxLength)
        {
            throw new WorkerProtocolException(code);
        }
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
