namespace RuBackend.Diagnostics;

using System.Text.Json.Serialization;

public sealed record DiagnosticEvent(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("ts")] double Ts,
    [property: JsonPropertyName("iso")] string Iso,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("event_code")] string EventCode,
    [property: JsonPropertyName("correlation_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId = null,
    [property: JsonPropertyName("duration_ms")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DurationMs = null,
    [property: JsonPropertyName("status")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("error_code")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
    [property: JsonPropertyName("exception_kind")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionKind = null);
