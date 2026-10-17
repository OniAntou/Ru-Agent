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

    /// <summary>
    /// Opens (or reuses) the run's gateway against the authority. The runtime
    /// open is awaited, so cancellation of the creating run propagates instead
    /// of parking a thread-pool thread on the 10s envelope.
    /// </summary>
    Task<IToolGateway> CreateAsync(
        string workspaceRoot,
        PolicySnapshot policySnapshot,
        RunId runId,
        string? agentSessionId,
        CancellationToken cancellationToken);

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

    /// <summary>
    /// Drops the run's Host-side state and closes its authority run lease.
    /// The close is awaited so teardown orders deterministically; it stays
    /// best effort (an authority that already closed the run is not an error).
    /// </summary>
    Task CloseRunAsync(string runId);
}
