using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public sealed record CreateAgentRunRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_path")] string WorkspacePath,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("preset_selection_version")] long? PresetSelectionVersion,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey);

public sealed record CreateAgentRunResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

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
    [property: JsonPropertyName("display_identity_hash")] string? DisplayIdentityHash = null);

public sealed record TrustAgentRunRequest(
    [property: JsonPropertyName("trust_request_id")] string TrustRequestId,
    [property: JsonPropertyName("display_identity_hash")] string DisplayIdentityHash,
    [property: JsonPropertyName("decision")] string Decision);

public sealed record AgentRunTrustResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("trust_request_id")] string TrustRequestId,
    [property: JsonPropertyName("status")] string Status);

public sealed record AgentErrorResponse(
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record SessionRunsResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<GetAgentRunResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record CreatePolicyRequest(
    [property: JsonPropertyName("rule")] object? Rule);

public sealed record PolicyListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<object> Items);

public sealed record SessionPresetRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("expected_version")] long ExpectedVersion);

public sealed record SessionPresetResponse(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public sealed record PolicyMatcherDto(
    [property: JsonPropertyName("tool_id")] string? ToolId,
    [property: JsonPropertyName("tool_version")] string? ToolVersion,
    [property: JsonPropertyName("canonical_path_pattern")] string? CanonicalPathPattern,
    [property: JsonPropertyName("resolved_executable")] string? ResolvedExecutable,
    [property: JsonPropertyName("argv_pattern")] IReadOnlyList<string>? ArgvPattern,
    [property: JsonPropertyName("cwd_pattern")] string? CwdPattern,
    [property: JsonPropertyName("operation_class")] string? OperationClass,
    [property: JsonPropertyName("network_intent")] string? NetworkIntent);

public sealed record CreatePolicyRuleRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("matcher")] PolicyMatcherDto Matcher,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("run_id")] string? RunId);

public sealed record PolicyRuleResponse(
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("matcher")] PolicyMatcherDto Matcher,
    [property: JsonPropertyName("creation_order")] long CreationOrder,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("version")] int Version);

public sealed record ApprovalRequest(
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("command")] string? Command);
