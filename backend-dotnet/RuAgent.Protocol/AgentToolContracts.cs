using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public static class ToolResultStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Denied = "denied";
    public const string ApprovalRequired = "approval_required";
    public const string TimedOut = "timed_out";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [Succeeded, Failed, Cancelled, Denied, ApprovalRequired, TimedOut],
        StringComparer.Ordinal);
}

public sealed record ToolRequest(
    string RequestId,
    string SessionId,
    string ToolId,
    string ToolVersion,
    JsonObject Arguments,
    IReadOnlySet<string> RequiredScopes,
    string IdempotencyKey,
    int DeadlineMs);

public sealed record ToolResult(
    string RequestId,
    string SessionId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    int DurationMs);

public sealed record AgentStatusEvent(
    string SessionId,
    string RequestId,
    string Status,
    string SafeSummary,
    int DurationMs);
