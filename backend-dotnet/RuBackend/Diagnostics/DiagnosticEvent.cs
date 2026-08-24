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
    [property: JsonPropertyName("exception_kind")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionKind = null,
    [property: JsonPropertyName("tool_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolId = null,
    [property: JsonPropertyName("tool_version")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolVersion = null,
    [property: JsonPropertyName("verification_state")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerificationState = null,
    [property: JsonPropertyName("endpoint_role")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndpointRole = null,
    [property: JsonPropertyName("endpoint_hash")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndpointHash = null,
    [property: JsonPropertyName("run_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonPropertyName("model_step_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelStepId = null,
    [property: JsonPropertyName("tool_call_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolCallId = null,
    [property: JsonPropertyName("execution_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionId = null,
    [property: JsonPropertyName("byte_count")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteCount = null,
    [property: JsonPropertyName("truncated")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null);
