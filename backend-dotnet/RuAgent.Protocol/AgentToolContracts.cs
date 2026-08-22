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

public static class ToolVerificationStates
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string Failed = "failed";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [NotRequired, Pending, Verified, Failed],
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
    int DurationMs,
    string VerificationState = ToolVerificationStates.NotRequired,
    string? EndpointRole = null,
    string? EndpointHash = null,
    bool AdapterInvoked = false);

public sealed record AgentToolCapability(
    string ToolId,
    string ToolVersion,
    IReadOnlySet<string> Scopes,
    string Risk,
    string Availability,
    bool ModelEnabled);

public sealed record AgentStatusEvent(
    string SessionId,
    string RequestId,
    string Status,
    string SafeSummary,
    int DurationMs);
