using System.Text.Json.Nodes;

namespace RuBackend.Agent;

public sealed record AgentSessionGrant(
    string SessionId,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> SkillIds,
    DateTimeOffset ExpiresAt);

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
    IReadOnlyDictionary<string, object?> Arguments,
    int DeadlineMs);

public sealed record AgentToolResult(
    string RequestId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary);
