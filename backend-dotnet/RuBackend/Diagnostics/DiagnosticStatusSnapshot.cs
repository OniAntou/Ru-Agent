namespace RuBackend.Diagnostics;

using System.Text.Json.Serialization;

public sealed record DiagnosticStatusSnapshot(
    [property: JsonPropertyName("queue_depth")] int QueueDepth,
    [property: JsonPropertyName("enqueued_count")] long EnqueuedCount,
    [property: JsonPropertyName("dropped_count")] long DroppedCount,
    [property: JsonPropertyName("write_failure_count")] long WriteFailureCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("active_files_count")] int ActiveFilesCount);
