namespace RuApp.Services;

public sealed record AgentApprovalModel(
    string ApprovalId,
    string SessionId,
    string ToolId,
    string SafeAction,
    string Scope,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset? now = null) => ExpiresAt <= (now ?? DateTimeOffset.UtcNow);
}

public sealed record AgentApprovalResult(
    string ApprovalId,
    string SessionId,
    string Status,
    string SafeSummary);
