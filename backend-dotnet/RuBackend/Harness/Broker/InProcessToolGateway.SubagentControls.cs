using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGateway
{
    private async Task<ToolProposalDecision?> EvaluateNativeSubagentAsync(ToolProposal proposal, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsControlTool(proposal.ToolId) && proposal.ToolId != "agent_apply") return null;
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var lease = Lease;
        var kind = proposal.ToolId == "agent_wait" ? ToolAccessKind.ReadOnly : ToolAccessKind.Mutation;
        if (proposal.RunId.Value != _runId)
            return ToolProposalDecision.Deny(kind, "subagent_control_binding_invalid", gatewayGeneration: lease.Generation);
        if (_subagents is null || !await _runtime.IsToolAdvertisedAsync(lease.SessionId, _runId, proposal.ToolId, ct).ConfigureAwait(false))
            return ToolProposalDecision.Deny(kind, "phase_not_available", gatewayGeneration: lease.Generation);
        if (proposal.ToolId == "agent_apply") return await _subagents.Value.EvaluateApplyAsync(proposal, ct).ConfigureAwait(false);
        try { await _subagents.Value.ValidateAsync(proposal, ct).ConfigureAwait(false); }
        catch (SubagentControlRejectedException error)
        { return ToolProposalDecision.Deny(kind, error.Code, gatewayGeneration: lease.Generation); }
        // Passing metadata validation grants nothing: normal Broker evaluation,
        // approval and exact token consumption still follow in EvaluateAsync.
        return null;
    }

    private async Task<ToolExecutionReceipt> ExecuteNativeSubagentAsync(PendingExecution pending,
        PreparedToolExecution execution, string sessionId, CancellationToken ct)
    {
        if (_subagents is null) return BrokerGatewayContracts.FailedReceipt(execution, "phase_not_available");
        var entered = false;
        try
        {
            return await _runtime.AuthorizeSubagentControlAsync(Request(sessionId, AgentMessageTypes.ToolExecute, new JsonObject
            {
                ["run_id"] = _runId, ["tool_id"] = pending.ToolId, ["tool_version"] = pending.ToolVersion,
                ["tool_call_id"] = pending.Proposal.ToolCallId.Value,
                ["authorization_token"] = pending.AuthorizationToken,
                ["arguments"] = BrokerGatewayContracts.ParseArgumentsJson(pending.ArgumentsJson),
            }), (intent, token) =>
            {
                entered = true;
                return _subagents.Value.DispatchAsync(intent, pending.Proposal, execution, token);
            }, ct).ConfigureAwait(false);
        }
        catch (SubagentControlRejectedException error)
        { return BrokerGatewayContracts.FailedReceipt(execution, error.Code); }
        catch (OperationCanceledException)
        {
            return new(execution.ExecutionId, execution.ToolCallId, ToolExecutionOutcome.Cancelled,
                "subagent_control_canceled", null, entered && pending.AccessKind != ToolAccessKind.ReadOnly);
        }
        catch (AgentProtocolException error) when (!entered)
        { return BrokerGatewayContracts.FailedReceipt(execution, error.Code); }
        catch (Exception error) when (error is AgentProtocolException or RuAgent.Harness.Persistence.HarnessStoreException
            or SubagentConfigurationException or RuAgent.Broker.Subagents.Workspaces.SubagentWorkspaceException
            or IOException or UnauthorizedAccessException)
        {
            // A callback may have committed an identity/message/tombstone before
            // reporting failed. Preserve uncertainty; do not silently rerun it.
            var effects = entered && pending.AccessKind != ToolAccessKind.ReadOnly;
            var code = error switch
            {
                AgentProtocolException failure => failure.Code,
                RuAgent.Harness.Persistence.HarnessStoreException failure => failure.Code,
                SubagentConfigurationException failure => failure.Code,
                RuAgent.Broker.Subagents.Workspaces.SubagentWorkspaceException failure => failure.Code,
                _ => "subagent_control_io_failed",
            };
            if (string.IsNullOrEmpty(code) || code.Length > 128 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) code = "subagent_control_failed";
            return new(execution.ExecutionId, execution.ToolCallId,
                effects ? ToolExecutionOutcome.OutcomeUnknown : ToolExecutionOutcome.Failed,
                code, null, effects);
        }
    }
}
