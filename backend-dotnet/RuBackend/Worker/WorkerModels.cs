using System.Text.Json.Nodes;

namespace RuBackend.Worker;

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

public sealed record WorkerRuntimeRequest(
    string RequestId,
    string Method,
    string PathAndQuery,
    string? ContentType,
    byte[] Body);

public sealed record WorkerRuntimeResponse(
    int StatusCode,
    string? ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record WorkerVoiceStreamRequest(
    string RequestId,
    string Text,
    string? VoiceLabel,
    double? Speed,
    string VoiceStyleId);

public sealed record WorkerVoiceSignal(
    string RequestId,
    string Signal,
    string? State = null);
