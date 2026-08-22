namespace RuApp.Services;

public sealed record AgentApprovalModel(
    string RequestId,
    string SessionId,
    string SafeAction,
    string Scope,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset? now = null) => ExpiresAt <= (now ?? DateTimeOffset.UtcNow);
}
