using System.Text.Json.Nodes;

namespace RuBackend.Agent;

public sealed record AgentSessionGrant(
    string SessionId,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> SkillIds,
    DateTimeOffset ExpiresAt);

public sealed record AgentApprovalRequest(
    string RequestId,
    string SessionId,
    string ToolId,
    string ToolVersion,
    string ArgumentsHash,
    string SafeSummary,
    DateTimeOffset ExpiresAt);

public sealed record AgentApprovalResponse(
    string RequestId,
    string SessionId,
    string ArgumentsHash,
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
