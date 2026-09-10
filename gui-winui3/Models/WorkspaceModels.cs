using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public sealed record WorkspaceConcurrencyPolicyDto(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("max_concurrent_runs")] int MaxConcurrentRuns,
        [property: JsonPropertyName("lease_ttl_seconds")] int LeaseTtlSeconds,
        [property: JsonPropertyName("merge_text_conflicts")] bool MergeTextConflicts,
        [property: JsonPropertyName("external_writer_detection")] bool ExternalWriterDetection);

    public sealed record WorkspaceConcurrencyResponseDto(
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("policy")] WorkspaceConcurrencyPolicyDto Policy,
        [property: JsonPropertyName("active_claim_count")] int ActiveClaimCount,
        [property: JsonPropertyName("updated_at_utc")] string UpdatedAtUtc);

    public sealed record WorkspaceActivityItemDto(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("writer_kind")] string? WriterKind,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
        [property: JsonPropertyName("details")] string? Details);

    public sealed record WorkspaceActivityResponseDto(
        [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceActivityItemDto> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    public sealed record WorkspaceChangeItemDto(
        [property: JsonPropertyName("change_id")] string ChangeId,
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("before_sha256")] string? BeforeSha256,
        [property: JsonPropertyName("after_sha256")] string? AfterSha256,
        [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc);

    public sealed record WorkspaceChangesResponseDto(
        [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceChangeItemDto> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    public sealed record WorkspaceChangeRestoreResponseDto(
        [property: JsonPropertyName("change_id")] string ChangeId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("restored_sha256")] string RestoredSha256,
        [property: JsonPropertyName("restore_change_id")] string RestoreChangeId);
}
