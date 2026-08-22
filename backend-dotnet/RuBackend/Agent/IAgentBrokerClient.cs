using System.Text.Json.Nodes;

namespace RuBackend.Agent;

public interface IAgentBrokerClient : IAsyncDisposable
{
    Task<IReadOnlyList<RuAgent.Protocol.AgentToolCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    Task<AgentSessionGrant> OpenSessionAsync(
        string sessionId,
        IReadOnlySet<string> scopes,
        IReadOnlySet<string> skillIds,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken);

    Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCall call,
        CancellationToken cancellationToken);

    Task<AgentToolResult> RespondToApprovalAsync(
        AgentApprovalResponse response,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken);

    Task CancelAsync(
        string sessionId,
        string requestId,
        string? correlationId,
        CancellationToken cancellationToken);

    Task CloseSessionAsync(
        string sessionId,
        string? correlationId,
        CancellationToken cancellationToken);
}
