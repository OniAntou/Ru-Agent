using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public sealed record AgentEnvelope(
    int ProtocolVersion,
    string RequestId,
    string MessageType,
    string SessionId,
    string? CorrelationId,
    int DeadlineMs,
    JsonObject Payload);
