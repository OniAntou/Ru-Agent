using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public sealed record DiagnosticEventDto(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("ts")] double Ts,
        [property: JsonPropertyName("iso")] string Iso,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("component")] string Component,
        [property: JsonPropertyName("event_code")] string EventCode,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId,
        [property: JsonPropertyName("duration_ms")] double? DurationMs,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("exception_kind")] string? ExceptionKind);

    public sealed record DiagnosticLogResultDto(
        [property: JsonPropertyName("events")] IReadOnlyList<DiagnosticEventDto> Events,
        [property: JsonPropertyName("cursor")] string? Cursor);

    public sealed record DiagnosticWriterStatusDto(
        [property: JsonPropertyName("queue_depth")] int QueueDepth,
        [property: JsonPropertyName("enqueued_count")] long EnqueuedCount,
        [property: JsonPropertyName("dropped_count")] long DroppedCount,
        [property: JsonPropertyName("write_failure_count")] long WriteFailureCount,
        [property: JsonPropertyName("total_bytes")] long TotalBytes,
        [property: JsonPropertyName("active_files_count")] int ActiveFilesCount);

    public sealed record DiagnosticStatusDto(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("total_log_files")] int TotalLogFiles,
        [property: JsonPropertyName("total_size_bytes")] long TotalSizeBytes,
        [property: JsonPropertyName("writer")] DiagnosticWriterStatusDto? Writer = null);
}
