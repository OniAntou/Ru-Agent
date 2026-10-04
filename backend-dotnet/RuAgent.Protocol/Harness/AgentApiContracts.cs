using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public sealed record CreateAgentRunRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_path")] string WorkspacePath,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("preset_selection_version")] long? PresetSelectionVersion,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    /// <summary>Up to 4 data:image/(png|jpeg|webp);base64,... images (decoded total &lt;= 20MB, per-image &lt;= 4MB).</summary>
    [property: JsonPropertyName("image_attachments")] System.Collections.Generic.IReadOnlyList<string>? ImageAttachments = null);

public sealed record CreateAgentRunResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("history_wait_kind")] string? HistoryWaitKind = null,
    [property: JsonPropertyName("policy_snapshot_version")] long PolicySnapshotVersion = 0,
    [property: JsonPropertyName("policy_snapshot_sha256")] string PolicySnapshotSha256 = "",
    [property: JsonPropertyName("version")] long Version = 0,
    [property: JsonPropertyName("last_sequence")] long LastSequence = 0,
    [property: JsonPropertyName("terminal_at")] string? TerminalAt = null,
    [property: JsonPropertyName("terminal_error_code")] string? TerminalErrorCode = null,
    [property: JsonPropertyName("trust_request_id")] string? TrustRequestId = null,
    [property: JsonPropertyName("display_identity_hash")] string? DisplayIdentityHash = null,
    [property: JsonPropertyName("provider_backend")] string? ProviderBackend = null,
    [property: JsonPropertyName("provider_model")] string? ProviderModel = null);

public sealed record GetAgentRunResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("history_wait_kind")] string? HistoryWaitKind,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("policy_snapshot_version")] long PolicySnapshotVersion,
    [property: JsonPropertyName("policy_snapshot_sha256")] string PolicySnapshotSha256,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("last_sequence")] long LastSequence,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("terminal_at")] string? TerminalAt,
    [property: JsonPropertyName("terminal_error_code")] string? TerminalErrorCode,
    [property: JsonPropertyName("trust_request_id")] string? TrustRequestId = null,
    [property: JsonPropertyName("display_identity_hash")] string? DisplayIdentityHash = null,
    [property: JsonPropertyName("provider_backend")] string? ProviderBackend = null,
    [property: JsonPropertyName("provider_model")] string? ProviderModel = null);

public sealed record TrustAgentRunRequest(
    [property: JsonPropertyName("trust_request_id")] string TrustRequestId,
    [property: JsonPropertyName("display_identity_hash")] string DisplayIdentityHash,
    [property: JsonPropertyName("decision")] string Decision);

public sealed record AgentRunTrustResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("trust_request_id")] string TrustRequestId,
    [property: JsonPropertyName("status")] string Status);

public sealed record CompactAgentRunRequest(
    [property: JsonPropertyName("strategy")] string? Strategy = null);

public sealed record CompactAgentRunResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("compaction_id")] string? CompactionId,
    [property: JsonPropertyName("before_tokens")] long? BeforeTokens,
    [property: JsonPropertyName("after_tokens")] long? AfterTokens,
    [property: JsonPropertyName("run_version")] long RunVersion);

public sealed record DispatchAgentCommandRequest(
    [property: JsonPropertyName("text")] string Text);

public sealed record DispatchAgentCommandResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("data")] object? Data,
    [property: JsonPropertyName("run_version")] long RunVersion);

public sealed record AgentErrorResponse(
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string? Message = null,
    /// <summary>Run that already holds the conflicting workspace/session, on a busy conflict.</summary>
    [property: JsonPropertyName("blocking_run_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockingRunId = null,
    [property: JsonPropertyName("blocking_run_status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockingRunStatus = null);

public sealed record SessionRunsResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<GetAgentRunResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record WorkspaceActivityResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceActivityItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record WorkspaceChangesResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceChangeItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record WorkspaceConcurrencyUpdateRequest(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("max_concurrent_runs")] int MaxConcurrentRuns = 1,
    [property: JsonPropertyName("lease_ttl_seconds")] int LeaseTtlSeconds = 30,
    [property: JsonPropertyName("merge_text_conflicts")] bool MergeTextConflicts = true,
    [property: JsonPropertyName("external_writer_detection")] bool ExternalWriterDetection = true);

public sealed record WorkspaceConcurrencyResponse(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("policy")] WorkspaceConcurrencyPolicy Policy,
    [property: JsonPropertyName("active_claim_count")] int ActiveClaimCount,
    [property: JsonPropertyName("updated_at_utc")] string UpdatedAtUtc);
