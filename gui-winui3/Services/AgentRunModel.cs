using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed record AgentRunModel(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string Status,
    string? HistoryWaitKind,
    string Preset,
    long PolicySnapshotVersion,
    string PolicySnapshotSha256,
    long Version,
    long LastSequence,
    string CreatedAt,
    string UpdatedAt,
    string? TerminalAt,
    string? TerminalErrorCode,
    string? TrustRequestId,
    string? DisplayIdentityHash)
{
    public static AgentRunModel FromCreateResponse(CreateAgentRunResponse response) => new(
        response.RunId,
        response.SessionId,
        response.WorkspaceId,
        response.Status,
        response.HistoryWaitKind,
        response.Preset,
        response.PolicySnapshotVersion,
        response.PolicySnapshotSha256,
        response.Version,
        response.LastSequence,
        response.CreatedAt,
        response.UpdatedAt,
        response.TerminalAt,
        response.TerminalErrorCode,
        response.TrustRequestId,
        response.DisplayIdentityHash);

    public static AgentRunModel FromGetResponse(GetAgentRunResponse response) => new(
        response.RunId,
        response.SessionId,
        response.WorkspaceId,
        response.Status,
        response.HistoryWaitKind,
        response.Preset,
        response.PolicySnapshotVersion,
        response.PolicySnapshotSha256,
        response.Version,
        response.LastSequence,
        response.CreatedAt,
        response.UpdatedAt,
        response.TerminalAt,
        response.TerminalErrorCode,
        response.TrustRequestId,
        response.DisplayIdentityHash);
}
