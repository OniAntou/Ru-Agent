using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;

namespace RuBackend.Agent;

public interface IAgentBrokerClient : IAsyncDisposable
{
    Task<IReadOnlyList<RuAgent.Protocol.AgentToolCapability>> GetCapabilitiesAsync(
        CapabilityProfile profile,
        CancellationToken cancellationToken);

    Task<AgentSessionGrant> OpenSessionAsync(
        string sessionId,
        IReadOnlySet<string> scopes,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken);

    Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCall call,
        CapabilityProfile profile,
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
