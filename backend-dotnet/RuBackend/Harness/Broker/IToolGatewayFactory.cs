using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;

namespace RuBackend.Harness.Broker;

/// <summary>
/// The factory seam the Host reaches the execution authority through. The
/// in-process factory (<see cref="InProcessToolGatewayFactory"/>) exposes the
/// member surface call sites need without knowing the implementation.
/// </summary>
public interface IToolGatewayFactory : IAsyncDisposable
{
    bool IsConfigured { get; }

    IToolGateway Create(
        string workspaceRoot,
        PolicySnapshot policySnapshot,
        RunId runId,
        string? agentSessionId = null);

    Task<JsonObject> ListSessionTasksAsync(
        string runId,
        string? killExecutionId,
        CancellationToken cancellationToken);

    Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string runId,
        string hostApprovalId,
        string command,
        CancellationToken cancellationToken);

    Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes one Agent session on the authority: its run leases and pending
    /// approvals are dropped, and the execution service stops terminal tasks
    /// and clears approvals, todos and output spools.
    /// Idempotent; a session the authority never opened is a no-op.
    /// </summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken);

    void CloseRun(string runId);
}
