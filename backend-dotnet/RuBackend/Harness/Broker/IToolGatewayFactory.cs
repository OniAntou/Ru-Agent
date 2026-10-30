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

    Task ReclaimSubagentWorkspaceAsync(RuAgent.Protocol.Subagents.SubagentCollectionSource source, CancellationToken ct) =>
        Task.FromException(new ToolTransportException("subagent_cleanup_unavailable"));

    Task<RuAgent.Protocol.Subagents.SubagentApplyInspection> InspectSubagentApplyAsync(ToolProposal proposal,
        RuAgent.Protocol.Subagents.SubagentChangeSet changes, RuAgent.Protocol.Subagents.SubagentApplyJournalState journal,
        CancellationToken ct) => Task.FromException<RuAgent.Protocol.Subagents.SubagentApplyInspection>(
            new ToolTransportException("subagent_inspection_unavailable"));

    Task<ToolProposalDecision> EvaluateSubagentApplyAsync(ToolProposal proposal,
        RuAgent.Protocol.Subagents.SubagentChangeSet changes, CancellationToken ct) =>
        Task.FromException<ToolProposalDecision>(new ToolTransportException("subagent_apply_unavailable"));

    Task<RuAgent.Protocol.Subagents.SubagentApplyOutcome> ApplySubagentChangesAsync(ToolProposal proposal,
        RuAgent.Protocol.Subagents.SubagentChangeSet changes, string canonicalIntentToken, long? gatewayGeneration,
        RuAgent.Protocol.Subagents.ISubagentApplyJournal journal, CancellationToken ct) =>
        Task.FromException<RuAgent.Protocol.Subagents.SubagentApplyOutcome>(new ToolTransportException("subagent_apply_unavailable"));

    Task<RuAgent.Broker.Subagents.Workspaces.SubagentWorkspaceManifest> ProvisionSubagentWorkspaceAsync(
        RunId parentRunId, string agentId, string stateDirectory, CancellationToken cancellationToken,
        RuAgent.Protocol.Harness.BrokerPolicySnapshotWire? parentCeiling = null,
        RuAgent.Protocol.Subagents.ISubagentWorkspaceAllocationObserver? observer = null) =>
        Task.FromException<RuAgent.Broker.Subagents.Workspaces.SubagentWorkspaceManifest>(
            new InvalidOperationException("subagent_workspace_unavailable"));

    Task ReclaimSubagentAllocationAsync(RuAgent.Protocol.Subagents.SubagentWorkspaceAllocation evidence,
        string stateDirectory, CancellationToken ct) =>
        Task.FromException(new ToolTransportException("subagent_cleanup_unavailable"));

    Task<RuAgent.Protocol.Subagents.SubagentChangeSet> CollectSubagentChangesAsync(
        RuAgent.Protocol.Subagents.SubagentCollectionSource source, long resultVersion,
        IReadOnlyDictionary<string, string> appliedBaseHashes, CancellationToken ct) =>
        Task.FromException<RuAgent.Protocol.Subagents.SubagentChangeSet>(
            new RuAgent.Protocol.AgentProtocolException("subagent_collection_unavailable"));

    Task BindDelegationAsync(RunId parentRunId, RunId childRunId,
        RuAgent.Protocol.Subagents.SubagentRole role, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("subagent_delegation_unavailable"));

    Task BindDelegationAsync(RunId parentRunId, RunId childRunId,
        RuAgent.Protocol.Subagents.SubagentRole role,
        RuAgent.Protocol.Subagents.SubagentParentPolicyIdentity expectedParentPolicy, CancellationToken cancellationToken,
        RuAgent.Protocol.Harness.BrokerPolicySnapshotWire? parentCeiling = null) =>
        Task.FromException(new InvalidOperationException("subagent_delegation_unavailable"));

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

    /// <summary>
    /// Reads one pending approval without consuming it or issuing a token. The Host checks <c>Known</c> before it
    /// commits an allow, and refuses allow_always unless <c>Rememberable</c>. The default answers unknown, so an
    /// authority that cannot answer fails closed.
    /// </summary>
    Task<BrokerApprovalProbe> ProbeApprovalAsync(string runId, string hostApprovalId, CancellationToken cancellationToken) =>
        Task.FromResult(BrokerApprovalProbe.Unknown);

    /// <summary>
    /// Resolves a pending approval on the authority. The remember plan the Broker built for that approval comes
    /// from the same consuming call that issues the one-time token, and is null when nothing was authorized.
    /// </summary>
    Task<(bool Authorized, string? AuthorizationToken, RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)> RespondApprovalAsync(
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
