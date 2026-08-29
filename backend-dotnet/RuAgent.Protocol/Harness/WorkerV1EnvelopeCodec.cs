using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Harness;

public static class WorkerV1EnvelopeCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static WorkerV1Envelope Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictUtf8.GetByteCount(json) > WorkerV1Contracts.MaxFrameBytes)
        {
            throw new AgentProtocolException("message_too_large");
        }

        DetectDuplicateTopLevelKeys(json);

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

        var version = ReadInt(obj, "v", "invalid_version");
        if (version != WorkerV1Contracts.ProtocolVersion)
        {
            throw new AgentProtocolException("unsupported_version");
        }

        var id = ReadString(obj, "id", "invalid_id");
        if (id.Length > WorkerV1Contracts.MaxRequestIdChars)
        {
            throw new AgentProtocolException("invalid_id");
        }

        var type = ReadString(obj, "type", "unknown_type");
        if (!WorkerV1Contracts.KnownTypes.Contains(type))
        {
            throw new AgentProtocolException("unknown_type");
        }

        long? sequence = null;
        if (obj.ContainsKey("seq"))
        {
            var parsedSequence = ReadLong(obj, "seq", "invalid_sequence");
            if (parsedSequence < 0 || parsedSequence > WorkerV1Contracts.MaxSequence)
            {
                throw new AgentProtocolException("invalid_sequence");
            }

            sequence = parsedSequence;
        }

        if (WorkerV1Contracts.OrderedResponseTypes.Contains(type) && sequence is null)
        {
            throw new AgentProtocolException("missing_sequence");
        }

        string? correlationId = null;
        var hasSnakeCaseCorrelation = obj.ContainsKey("correlation_id");
        var hasCamelCaseCorrelation = obj.ContainsKey("correlationId");
        if (hasSnakeCaseCorrelation && hasCamelCaseCorrelation)
        {
            throw new AgentProtocolException("duplicate_field");
        }

        var correlationKey = hasSnakeCaseCorrelation ? "correlation_id" : "correlationId";
        if (obj.ContainsKey(correlationKey))
        {
            correlationId = ReadString(obj, correlationKey, "invalid_correlation_id");
            if (correlationId.Length > WorkerV1Contracts.MaxCorrelationIdChars)
            {
                throw new AgentProtocolException("invalid_correlation_id");
            }
        }

        if (obj["payload"] is not JsonObject payload)
        {
            throw new AgentProtocolException("invalid_payload");
        }

        var result = new WorkerV1Envelope(
            version,
            id,
            type,
            sequence,
            correlationId,
            (JsonObject)payload.DeepClone());
        EnsureSerializedSize(result);
        return result;
    }

    public static string Serialize(WorkerV1Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateValue(envelope);

        var obj = new JsonObject
        {
            ["v"] = envelope.Version,
            ["id"] = envelope.Id,
            ["type"] = envelope.Type,
            ["payload"] = envelope.Payload.DeepClone(),
        };
        if (envelope.Sequence is not null)
        {
            obj["seq"] = envelope.Sequence.Value;
        }

        if (envelope.CorrelationId is not null)
        {
            obj["correlation_id"] = envelope.CorrelationId;
        }

        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (StrictUtf8.GetByteCount(json) > WorkerV1Contracts.MaxFrameBytes)
        {
            throw new AgentProtocolException("message_too_large");
        }

        return json;
    }

    private static void ValidateValue(WorkerV1Envelope envelope)
    {
        if (envelope.Version != WorkerV1Contracts.ProtocolVersion)
        {
            throw new AgentProtocolException("unsupported_version");
        }

        if (string.IsNullOrWhiteSpace(envelope.Id)
            || envelope.Id.Length > WorkerV1Contracts.MaxRequestIdChars)
        {
            throw new AgentProtocolException("invalid_id");
        }

        if (!WorkerV1Contracts.KnownTypes.Contains(envelope.Type))
        {
            throw new AgentProtocolException("unknown_type");
        }

        if (WorkerV1Contracts.OrderedResponseTypes.Contains(envelope.Type)
            && envelope.Sequence is null)
        {
            throw new AgentProtocolException("missing_sequence");
        }

        if (envelope.Sequence is < 0 or > WorkerV1Contracts.MaxSequence)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        if (envelope.CorrelationId is not null
            && (string.IsNullOrWhiteSpace(envelope.CorrelationId)
                || envelope.CorrelationId.Length > WorkerV1Contracts.MaxCorrelationIdChars))
        {
            throw new AgentProtocolException("invalid_correlation_id");
        }

        ArgumentNullException.ThrowIfNull(envelope.Payload);
    }

    private static void EnsureSerializedSize(WorkerV1Envelope envelope)
    {
        _ = Serialize(envelope);
    }

    private static string ReadString(JsonObject obj, string key, string code)
    {
        if (obj[key] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new AgentProtocolException(code);
    }

    private static int ReadInt(JsonObject obj, string key, string code)
    {
        if (obj[key] is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new AgentProtocolException(code);
    }

    private static long ReadLong(JsonObject obj, string key, string code)
    {
        if (obj[key] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var result))
            {
                return result;
            }

            if (value.TryGetValue<int>(out var intResult))
            {
                return intResult;
            }
        }

        throw new AgentProtocolException(code);
    }

    private static void DetectDuplicateTopLevelKeys(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(StrictUtf8.GetBytes(json), new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var depth = 0;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName when depth == 1:
                        if (!keys.Add(reader.GetString()!))
                        {
                            throw new AgentProtocolException("duplicate_field");
                        }

                        break;
                }
            }
        }
        catch (AgentProtocolException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw new AgentProtocolException("invalid_json", error.Message);
        }
    }
}
