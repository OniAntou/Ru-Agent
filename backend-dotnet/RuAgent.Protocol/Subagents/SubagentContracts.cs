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
    public const int MaxModelSteps = 32;
    public const int MaxToolCalls = 64;
    public const int FamilyModelSteps = 128;
    public const int FamilyToolCalls = 256;
    public const int MaxWaitMilliseconds = 60_000;
    public const int MaxSnapshotFiles = 50_000;
    public const long MaxSnapshotBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(15);
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
