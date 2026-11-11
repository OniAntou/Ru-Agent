using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Subagents;

public static class SubagentLimits
{
    public const int MaxTaskCharacters = 16_384;
    public const int MaxContextBytes = 64 * 1024;
    public const int MaxInlineResultBytes = 32 * 1024;
    public const int MaxApprovalPreviewBytes = 8 * 1024;
    public const int MaxIdentities = 8;
    public const int MaxRunnable = 3;
    public const int MaxAttempts = 3;
    // Ten minutes, so a root waiting on long children parks instead of spending a
    // model step on every check-in.
    public const int MaxWaitMilliseconds = 600_000;
    public const int MaxSnapshotFiles = 50_000;
    public const long MaxSnapshotBytes = 256L * 1024 * 1024;
    /// <summary>How long an attempt may wait to be admitted before it is abandoned.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromHours(4);

    /// <summary>
    /// How long an admitted attempt keeps its row before it is reclaimed as stale. It is not a
    /// work limit: a child ends when it finishes, fails or its parent cancels it, and provider
    /// and tool calls carry their own timeouts. The deadline only bounds a row nobody is driving.
    /// </summary>
    public static readonly TimeSpan AttemptRunTimeout = TimeSpan.FromDays(30);
}

[JsonConverter(typeof(SubagentRoleJsonConverter))]
public enum SubagentRole { Coding, Review }

public sealed record SubagentSpawnRequest(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("context")] string? Context = null,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyName("provider_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderId = null);

public sealed record SubagentSendRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("message")] string Message);

public sealed record SubagentWaitRequest(
    [property: JsonPropertyName("agent_ids")] IReadOnlyList<string> AgentIds,
    [property: JsonPropertyName("timeout_ms")] int TimeoutMs = 30_000);

public sealed record SubagentCancelRequest([property: JsonPropertyName("agent_id")] string AgentId);

public sealed record SubagentApplyRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("result_version")] long ResultVersion);

public readonly record struct SubagentValidationResult(bool IsValid, string? ErrorCode)
{
    public static SubagentValidationResult Valid => new(true, null);
    public static SubagentValidationResult Invalid => new(false, "invalid_subagent_arguments");
}
