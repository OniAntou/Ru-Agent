using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Harness;

public sealed record WorkerV1Envelope(
    int Version,
    string Id,
    string Type,
    long? Sequence,
    string? CorrelationId,
    JsonObject Payload)
{
    public WorkerV1Envelope(
        int version,
        string id,
        string type,
        long? sequence,
        JsonObject payload)
        : this(version, id, type, sequence, null, payload)
    {
    }
}
