using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace RuBackend.Agent;

public sealed record AgentSessionGrant
{
    public AgentSessionGrant(
        string sessionId,
        IEnumerable<string> scopes,
        DateTimeOffset expiresAt)
    {
        SessionId = sessionId;
        Scopes = scopes.ToFrozenSet(StringComparer.Ordinal);
        ExpiresAt = expiresAt;
    }

    public string SessionId { get; }
    public IReadOnlySet<string> Scopes { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed record AgentApprovalRequest(
    string ApprovalId,
    string SessionId,
    string ToolId,
    string ToolVersion,
    string SafeAction,
    string Scope,
    DateTimeOffset ExpiresAt);

public sealed record AgentApprovalResponse(
    string ApprovalId,
    string SessionId,
    bool Approved);

public sealed record AgentToolCall(
    string RequestId,
    string SessionId,
    string CorrelationId,
    string ToolId,
    string ToolVersion,
    string CapabilityClass,
    IReadOnlyDictionary<string, object?> Arguments,
    int DeadlineMs);

public sealed record AgentToolResult(
    string RequestId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    int DurationMs,
    string VerificationState,
    string? EndpointRole,
    string? EndpointHash,
    bool AdapterInvoked);
