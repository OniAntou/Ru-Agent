using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Worker;

public sealed record WorkerV1Reply(
    string Type,
    JsonObject Payload,
    ReadOnlyMemory<byte> Binary = default);
