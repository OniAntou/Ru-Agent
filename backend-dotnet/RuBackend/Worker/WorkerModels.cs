using System.Text.Json.Nodes;

namespace RuBackend.Worker;

public sealed record WorkerChatRequest(
    string RequestId,
    string Prompt,
    string SessionId,
    IReadOnlyList<WorkerTurn> Turns,
    string MemoryContext,
    string VtsInstruction,
    JsonObject Settings);

public sealed record WorkerTurn(string Prompt, string Response);

public sealed record WorkerEvent(
    string RequestId,
    string Type,
    long? Sequence,
    JsonObject Payload,
    ReadOnlyMemory<byte> Binary);

public sealed record WorkerHello(
    int Version,
    IReadOnlySet<string> Capabilities);

public sealed record WorkerHealth(
    bool Ready,
    int Version,
    IReadOnlySet<string> Capabilities);

public sealed record VtsCatalog(
    IReadOnlySet<string> HotkeyCodes,
    IReadOnlySet<string> ExpressionCodes);

public sealed record WorkerFact(
    string Kind,
    string Content,
    string? Scope,
    double Confidence,
    double Importance);
