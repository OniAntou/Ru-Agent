using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Subagents;

public sealed record SubagentListResponse(
    [property: JsonPropertyName("root_run_id")] string RootRunId,
    [property: JsonPropertyName("root_status")] string RootStatus,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("agents")] IReadOnlyList<SubagentSummary> Agents,
    [property: JsonPropertyName("preparations")] IReadOnlyList<SubagentPreparationSummary> Preparations,
    [property: JsonPropertyName("unavailable_attempts")] int UnavailableAttempts,
    [property: JsonPropertyName("usage")] SubagentUsageSummary Usage);

public sealed record SubagentSummary(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("task")] string? Task,
    [property: JsonPropertyName("canceled")] bool Canceled,
    [property: JsonPropertyName("attempts")] IReadOnlyList<SubagentAttemptSummary> Attempts,
    [property: JsonPropertyName("results")] IReadOnlyList<SubagentResultSummary> Results);

public sealed record SubagentAttemptSummary(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("deadline_at")] DateTimeOffset? DeadlineAt,
    [property: JsonPropertyName("approval")] SubagentApprovalSummary? Approval = null,
    [property: JsonPropertyName("usage")] SubagentUsageSummary? Usage = null);

public sealed record SubagentApprovalSummary(
    [property: JsonPropertyName("approval_id")] string ApprovalId,
    [property: JsonPropertyName("tool_id")] string ToolId,
    [property: JsonPropertyName("arguments")] System.Text.Json.JsonElement Arguments,
    [property: JsonPropertyName("arguments_truncated")] bool ArgumentsTruncated = false);

public sealed record SubagentResultSummary(
    [property: JsonPropertyName("result_version")] long ResultVersion,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("apply_status")] string? ApplyStatus,
    [property: JsonPropertyName("applied_files")] int? AppliedFiles,
    [property: JsonPropertyName("acknowledged")] bool Acknowledged,
    [property: JsonPropertyName("user_action")] SubagentUserActionSummary? UserAction);

public sealed record SubagentUserActionSummary(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("approval_id")] string? ApprovalId,
    [property: JsonPropertyName("code")] string? Code);

public sealed record SubagentPreparationSummary(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("stage")] string Stage);

public sealed record SubagentUsageSummary(
    [property: JsonPropertyName("model_steps")] long ModelSteps,
    [property: JsonPropertyName("reported_steps")] long ReportedSteps,
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens,
    [property: JsonPropertyName("is_complete")] bool IsComplete);

public sealed record SubagentDetailResponse(
    [property: JsonPropertyName("root_run_id")] string RootRunId,
    [property: JsonPropertyName("agent")] SubagentSummary Agent,
    [property: JsonPropertyName("result")] SubagentResultPageResponse? Result);

public sealed record SubagentResultPageResponse(
    [property: JsonPropertyName("result_version")] long ResultVersion,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("total_bytes")] int TotalBytes,
    [property: JsonPropertyName("next_offset")] int? NextOffset);
