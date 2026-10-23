using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;

namespace RuBackend.Harness.Subagents;

/// <summary>Loads exact persisted coding results; execution and approval remain with the single Broker authority.</summary>
public sealed partial class SubagentApplyService(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteSubagentResultStore results, SqliteSubagentChangeSetStore changes, SqliteSubagentApplyJournal journal,
    IToolGatewayFactory gateways) : IDisposable
{
    private readonly KeyedAsyncLock<RunId> _operations = new();

    public async Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken ct)
    {
        using var operation = await _operations.AcquireAsync(proposal.RunId, ct).ConfigureAwait(false);
        var frozen = await LoadAsync(proposal, ct).ConfigureAwait(false);
        return await gateways.EvaluateSubagentApplyAsync(proposal, frozen, ct).ConfigureAwait(false);
    }

    public async Task<SubagentApplyOutcome> ApplyAsync(ToolProposal proposal, string canonicalIntentToken,
        long? gatewayGeneration, CancellationToken ct)
    {
        using var operation = await _operations.AcquireAsync(proposal.RunId, ct).ConfigureAwait(false);
        var frozen = await LoadAsync(proposal, ct).ConfigureAwait(false);
        return await gateways.ApplySubagentChangesAsync(proposal, frozen, canonicalIntentToken,
            gatewayGeneration, journal, ct).ConfigureAwait(false);
    }

    private async Task<SubagentChangeSet> LoadAsync(ToolProposal proposal, CancellationToken ct, bool inspection = false)
    {
        if (proposal.ToolId != "agent_apply" || proposal.ToolVersion != "1.0.0"
            || !SubagentContractValidator.Validate(proposal.ToolId, proposal.Arguments).IsValid)
            throw new HarnessStoreException("invalid_subagent_arguments");
        var root = await events.GetRunAsync(proposal.RunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_apply_root_inactive");
        if (root.State is not (HarnessRunState.Running or HarnessRunState.WaitingApproval
            or HarnessRunState.WaitingUser or HarnessRunState.WaitingSubagents)
            && !(inspection && root.State == HarnessRunState.ReconciliationRequired))
            throw new HarnessStoreException("subagent_apply_root_inactive");
        if (await agents.IsInternalSessionAsync(root.SessionId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("subagent_apply_ownership_denied");
        var agentId = proposal.Arguments.GetProperty("agent_id").GetString()!;
        var version = proposal.Arguments.GetProperty("result_version").GetInt64();
        var owner = (await agents.ListAsync(root.RunId, ct).ConfigureAwait(false)).SingleOrDefault(a => a.AgentId == agentId);
        if (owner is null || owner.Role != SubagentRole.Coding)
            throw new HarnessStoreException("subagent_apply_ownership_denied");
        var result = (await results.ListAsync(root.RunId, agentId, ct).ConfigureAwait(false)).SingleOrDefault(r => r.Version == version);
        if (result is null || result.State != HarnessRunState.Completed || result.Code is not null)
            throw new HarnessStoreException("subagent_apply_result_unavailable");
        var frozen = await changes.GetAsync(root.RunId, agentId, result.RunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_changes_missing");
        if (frozen.ResultVersion != version) throw new HarnessStoreException("subagent_apply_result_unavailable");
        return frozen;
    }

    public void Dispose() => _operations.Dispose();
}
