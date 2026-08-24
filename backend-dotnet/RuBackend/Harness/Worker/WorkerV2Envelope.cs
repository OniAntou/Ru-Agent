using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Worker;

public sealed record WorkerV2Envelope(
    int V,
    string Id,
    string Type,
    long? Seq,
    string? CorrelationId,
    JsonObject Payload)
{
    public WorkerV2Envelope(int v, string id, string type, long? seq, JsonObject payload)
        : this(v, id, type, seq, null, payload)
    {
    }
}

public static class WorkerV2EnvelopeCodec
{
    public static WorkerV2Envelope Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > WorkerV2Contracts.MaxFrameBytes)
        {
            throw new AgentProtocolException("message_too_large");
        }

        // Detect duplicate keys at top-level (at least for seq, v, id, type, payload, correlation_id)
        DetectDuplicateKeys(json);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new AgentProtocolException("invalid_json", error.Message);
        }

        if (node is not JsonObject obj)
        {
            throw new AgentProtocolException("invalid_envelope");
        }

        var allowed = new HashSet<string>(["v", "id", "type", "seq", "correlation_id", "payload"], StringComparer.Ordinal);
        var fields = obj.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var required = new HashSet<string>(["v", "id", "type", "payload"], StringComparer.Ordinal);
        if (!required.IsSubsetOf(fields) || !fields.IsSubsetOf(allowed))
        {
            throw new AgentProtocolException("invalid_envelope");
        }

        var version = ReadInt(obj, "v", "invalid_version");
        if (version != WorkerV2Contracts.ProtocolVersion)
        {
            throw new AgentProtocolException("unsupported_version");
        }

        var id = ReadString(obj, "id", "invalid_id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > WorkerV2Contracts.MaxRequestIdChars || !IsSafeIdentifier(id))
        {
            throw new AgentProtocolException("invalid_id");
        }

        var type = ReadString(obj, "type", "unsupported_message");
        if (!WorkerV2Contracts.MessageTypes.Contains(type))
        {
            throw new AgentProtocolException("unsupported_message");
        }

        long? seq = null;
        var hasSeq = obj.ContainsKey("seq");
        if (hasSeq)
        {
            seq = ReadLong(obj, "seq", "invalid_sequence");
            if (seq < 0 || seq > WorkerV2Contracts.MaxSequence)
            {
                throw new AgentProtocolException("invalid_sequence");
            }
        }

        if (WorkerV2Contracts.OrderedResponseTypes.Contains(type) && !hasSeq)
        {
            throw new AgentProtocolException("missing_sequence");
        }

        if (!WorkerV2Contracts.OrderedResponseTypes.Contains(type) && hasSeq)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        if (hasSeq && type == WorkerV2Contracts.HelloOk && seq != 0)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        if (hasSeq && WorkerV2Contracts.OrderedResponseTypes.Contains(type) && type != WorkerV2Contracts.HelloOk && seq < 1)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        string? correlationId = null;
        if (obj.ContainsKey("correlation_id"))
        {
            var cNode = obj["correlation_id"];
            if (cNode is JsonValue cv && cv.TryGetValue<string>(out var cStr) && !string.IsNullOrWhiteSpace(cStr))
            {
                if (cStr.Length > WorkerV2Contracts.MaxCorrelationIdChars || !IsSafeIdentifier(cStr))
                {
                    throw new AgentProtocolException("invalid_correlation_id");
                }

                correlationId = cStr;
            }
            else if (cNode is not null)
            {
                throw new AgentProtocolException("invalid_correlation_id");
            }
        }

        var payloadNode = obj["payload"];
        if (payloadNode is not JsonObject payload)
        {
            throw new AgentProtocolException("invalid_payload");
        }

        var envelope = new WorkerV2Envelope(version, id, type, seq, correlationId, (JsonObject)payload.DeepClone());
        // Re-validate via contracts for payload specifics (hello, start etc)
        WorkerV2Contracts.ValidateEnvelope(obj);
        return envelope;
    }

    public static string Serialize(WorkerV2Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var obj = new JsonObject
        {
            ["v"] = envelope.V,
            ["id"] = envelope.Id,
            ["type"] = envelope.Type,
            ["payload"] = envelope.Payload.DeepClone(),
        };
        if (envelope.Seq is not null)
        {
            obj["seq"] = envelope.Seq.Value;
        }

        if (envelope.CorrelationId is not null)
        {
            obj["correlation_id"] = envelope.CorrelationId;
        }

        WorkerV2Contracts.ValidateEnvelope(obj);
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (Encoding.UTF8.GetByteCount(json) > WorkerV2Contracts.MaxFrameBytes)
        {
            throw new AgentProtocolException("message_too_large");
        }

        return json;
    }

    private static void DetectDuplicateKeys(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == 1:
                    var name = reader.GetString()!;
                    if (!seen.Add(name))
                    {
                        throw new AgentProtocolException("duplicate_field");
                    }

                    break;
            }
        }
    }

    private static string ReadString(JsonObject obj, string key, string code)
    {
        var node = obj[key];
        if (node is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new AgentProtocolException(code);
    }

    private static int ReadInt(JsonObject obj, string key, string code)
    {
        var node = obj[key];
        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        if (node is JsonValue lv && lv.TryGetValue<long>(out var l) && l >= int.MinValue && l <= int.MaxValue)
        {
            return (int)l;
        }

        throw new AgentProtocolException(code);
    }

    private static long ReadLong(JsonObject obj, string key, string code)
    {
        var node = obj[key];
        if (node is JsonValue value && value.TryGetValue<long>(out var result))
        {
            return result;
        }

        if (node is JsonValue iv && iv.TryGetValue<int>(out var i))
        {
            return i;
        }

        throw new AgentProtocolException(code);
    }

    private static bool IsSafeIdentifier(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$");
}
