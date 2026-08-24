using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public static class AgentEnvelopeCodec
{
    public const int ProtocolVersion = 1;
    public const int ProtocolVersionV2 = 2;
    public const int MaxIdLength = 128;
    public const int MaxDeadlineMs = 300_000;

    public static string Serialize(AgentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(envelope);

        var json = new JsonObject
        {
            ["protocol_version"] = envelope.ProtocolVersion,
            ["message_type"] = envelope.MessageType,
            ["request_id"] = envelope.RequestId,
            ["session_id"] = envelope.SessionId,
            ["deadline_ms"] = envelope.DeadlineMs,
            ["payload"] = envelope.Payload.DeepClone(),
        };
        if (envelope.CorrelationId is not null)
        {
            json["correlation_id"] = envelope.CorrelationId;
        }

        return json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        });
    }

    public static AgentEnvelope Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new AgentProtocolException("invalid_envelope");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new AgentProtocolException("invalid_envelope", error.Message);
        }

        if (node is not JsonObject objectNode)
        {
            throw new AgentProtocolException("invalid_envelope");
        }

        var envelope = new AgentEnvelope(
            ReadInt(objectNode, "protocol_version", "invalid_version"),
            ReadString(objectNode, "request_id", "invalid_id"),
            ReadString(objectNode, "message_type", "unknown_type"),
            ReadString(objectNode, "session_id", "invalid_session"),
            ReadOptionalString(objectNode, "correlation_id"),
            ReadInt(objectNode, "deadline_ms", "invalid_deadline"),
            ReadPayload(objectNode));

        Validate(envelope);
        return envelope with { Payload = (JsonObject)envelope.Payload.DeepClone() };
    }

    public static void Validate(AgentEnvelope envelope)
    {
        if (envelope.ProtocolVersion != ProtocolVersion && envelope.ProtocolVersion != ProtocolVersionV2)
        {
            throw new AgentProtocolException("unsupported_version");
        }

        ValidateId(envelope.RequestId, "invalid_id");
        ValidateId(envelope.SessionId, "invalid_session");
        if (envelope.CorrelationId is not null)
        {
            ValidateId(envelope.CorrelationId, "invalid_correlation");
        }

        if (!AgentMessageTypes.Known.Contains(envelope.MessageType))
        {
            throw new AgentProtocolException("unknown_type");
        }

        if (envelope.DeadlineMs is < 1 or > MaxDeadlineMs)
        {
            throw new AgentProtocolException("invalid_deadline");
        }

        ArgumentNullException.ThrowIfNull(envelope.Payload);
        if (envelope.ProtocolVersion == ProtocolVersionV2)
        {
            ValidateV2Payload(envelope);
        }
    }

    private static void ValidateV2Payload(AgentEnvelope envelope)
    {
        // Broker v2 requires run lease fields for run.open and strict payload validation for other v2 types.
        // Minimal check: for run.open, payload must contain run_id, session_id, workspace_id, canonical_root, etc.
        // For other v2 messages, ensure they are not sent on v1-only paths; actual payload validation is done in BrokerSession.
        if (envelope.MessageType == AgentMessageTypes.RunOpen)
        {
            var payload = envelope.Payload;
            var required = new[] { "run_id", "session_id", "workspace_id", "canonical_root", "volume_serial", "root_file_id", "display_hash", "preset", "snapshot_version", "snapshot_hash", "expires_at" };
            foreach (var field in required)
            {
                if (!payload.ContainsKey(field))
                {
                    throw new AgentProtocolException("invalid_run_open");
                }
            }
        }
    }

    private static int ReadInt(JsonObject node, string property, string code)
    {
        try
        {
            return node[property]?.GetValue<int>()
                ?? throw new AgentProtocolException(code);
        }
        catch (InvalidOperationException error)
        {
            throw new AgentProtocolException(code, error.Message);
        }
        catch (FormatException error)
        {
            throw new AgentProtocolException(code, error.Message);
        }
    }

    private static string ReadString(JsonObject node, string property, string code)
    {
        try
        {
            var value = node[property]?.GetValue<string>();
            if (value is null)
            {
                throw new AgentProtocolException(code);
            }

            return value;
        }
        catch (InvalidOperationException error)
        {
            throw new AgentProtocolException(code, error.Message);
        }
        catch (FormatException error)
        {
            throw new AgentProtocolException(code, error.Message);
        }
    }

    private static string? ReadOptionalString(JsonObject node, string property)
    {
        if (!node.TryGetPropertyValue(property, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException error)
        {
            throw new AgentProtocolException("invalid_correlation", error.Message);
        }
    }

    private static JsonObject ReadPayload(JsonObject node)
    {
        if (node["payload"] is not JsonObject payload)
        {
            throw new AgentProtocolException("invalid_payload");
        }

        return payload;
    }

    private static void ValidateId(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength)
        {
            throw new AgentProtocolException(code);
        }
    }
}
