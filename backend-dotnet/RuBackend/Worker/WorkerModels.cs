using System.Text.Json.Nodes;

namespace RuBackend.Worker;

public sealed record WorkerChatRequest(
    string RequestId,
    string Prompt,
    string SessionId,
    IReadOnlyList<WorkerTurn> Turns,
    string MemoryContext,
    string VtsInstruction,
    JsonObject Settings)
{
    public Func<WorkerToolRequest, CancellationToken, Task<WorkerToolResult>>? ToolHandler { get; init; }
}

public sealed record WorkerTurn(string Prompt, string Response);

public sealed record WorkerToolRequest(
    string RequestId,
    string SessionId,
    string CorrelationId,
    string ToolId,
    string ToolVersion,
    string CapabilityClass,
    JsonObject Arguments,
    int DeadlineMs);

public sealed record WorkerToolResult(
    string RequestId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    int DurationMs,
    string VerificationState,
    string? EndpointRole,
    string? EndpointHash);

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
