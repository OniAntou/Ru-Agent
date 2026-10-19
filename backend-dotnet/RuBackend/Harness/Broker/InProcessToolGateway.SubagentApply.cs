using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGateway
{
    internal Task<SubagentApplyInspection> InspectSubagentApplyAsync(ToolProposal proposal,
        SubagentChangeSet changes, SubagentApplyJournalState journal, CancellationToken ct)
    {
        ValidateSubagentApply(proposal, changes);
        BrokerRunLeaseIdentity lease;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            lease = _lease;
        }
        return _runtime.InspectSubagentApplyAsync(lease.SessionId, _runId, changes, journal, ct);
    }

    /// <summary>Frozen result bytes arrive only from the Host store; proposals carry identity, never file content.</summary>
    internal async Task<ToolProposalDecision> EvaluateSubagentApplyAsync(ToolProposal proposal,
        SubagentChangeSet changes, CancellationToken ct)
    {
        ValidateSubagentApply(proposal, changes);
        BrokerRunLeaseIdentity lease;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            lease = _lease;
            generation = _generation;
        }
        var argsKey = BrokerGatewayContracts.ApprovalArgsKey(proposal.ToolId, proposal.Arguments);
        if (_approvals is not null && _approvals.TryTakeAuthorizationToken(_runId, lease, argsKey, out var cached)
            && cached is not null)
            return ToolProposalDecision.Allow(ToolAccessKind.Mutation, BrokerGatewayContracts.AuthorizationTokenPrefix + cached, generation);

        var result = await _runtime.EvaluateSubagentApplyAsync(lease.SessionId, _runId, changes, ct).ConfigureAwait(false);
        if (result.Error is not null) throw new ToolTransportException(result.Error.ErrorCode);
        var decision = result.Decision ?? throw new ToolTransportException("invalid_decision");
        return decision.Decision switch
        {
            "allow" => ToolProposalDecision.Allow(ToolAccessKind.Mutation,
                decision.AuthorizationToken ?? throw new ToolTransportException("missing_decision_token"), generation),
            "deny" => ToolProposalDecision.Deny(ToolAccessKind.Mutation, decision.ReasonCode, decision.SafeSummary, generation),
            "ask" => RegisterAsk(proposal, lease, argsKey,
                decision.ApprovalId ?? throw new ToolTransportException("missing_approval_id"),
                ToolAccessKind.Mutation, decision.ReasonCode, generation),
            _ => throw new ToolTransportException("invalid_decision"),
        };
    }

    internal Task<SubagentApplyOutcome> ApplySubagentChangesAsync(ToolProposal proposal, SubagentChangeSet changes,
        string canonicalIntentToken, long? gatewayGeneration, ISubagentApplyJournal journal, CancellationToken ct)
    {
        ValidateSubagentApply(proposal, changes);
        BrokerRunLeaseIdentity lease;
        lock (_sync)
        {
            if (IsDisposed || gatewayGeneration != _generation)
                throw new ToolTransportException("broker_lease_generation_changed");
            lease = _lease;
        }
        var token = canonicalIntentToken.StartsWith(BrokerGatewayContracts.AuthorizationTokenPrefix, StringComparison.Ordinal)
            ? canonicalIntentToken[BrokerGatewayContracts.AuthorizationTokenPrefix.Length..] : canonicalIntentToken;
        return _runtime.ApplySubagentChangesAsync(lease.SessionId, _runId, changes, token, journal, ct);
    }

    private void ValidateSubagentApply(ToolProposal proposal, SubagentChangeSet changes)
    {
        if (proposal.RunId.Value != _runId || changes.ParentRunId != _runId || proposal.ToolId != "agent_apply"
            || proposal.ToolVersion != "1.0.0" || !SubagentContractValidator.Validate(proposal.ToolId, proposal.Arguments).IsValid
            || proposal.Arguments.GetProperty("agent_id").GetString() != changes.AgentId
            || proposal.Arguments.GetProperty("result_version").GetInt64() != changes.ResultVersion)
            throw new ToolTransportException("invalid_subagent_arguments");
    }
}
