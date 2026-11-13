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
        if (proposal.ToolId == "agent_apply")
        {
            try { return await _subagents.Value.EvaluateApplyAsync(proposal, ct).ConfigureAwait(false); }
            catch (RuAgent.Harness.Persistence.HarnessStoreException error)
                when (SubagentControlFailures.IsDeterministic(error.Code))
            {
                // Ownership, result availability and argument checks run before the
                // apply journal can write. They are a policy denial, not a transport
                // failure that should fail or park the root run.
                return ToolProposalDecision.Deny(ToolAccessKind.Mutation, error.Code,
                    gatewayGeneration: lease.Generation);
            }
            catch (ToolTransportException error)
                when (error.Message is "subagent_apply_root_not_open" or "subagent_apply_unavailable")
            {
                // The root's gateway is gone or the apply adapter is unavailable.
                // This is a refusal before the apply journal, so let the model see a
                // failed tool result instead of turning a closed transport into a run
                // failure.
                return ToolProposalDecision.Deny(ToolAccessKind.Mutation, error.Message,
                    gatewayGeneration: lease.Generation);
            }
            catch (AgentProtocolException error) when (error.Code is "subagent_apply_denied"
                or "subagent_apply_root_not_open" or "subagent_apply_unavailable")
            {
                return ToolProposalDecision.Deny(ToolAccessKind.Mutation, error.Code,
                    gatewayGeneration: lease.Generation);
            }
        }
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
            var receipt = await _runtime.AuthorizeSubagentControlAsync(Request(sessionId, AgentMessageTypes.ToolExecute, new JsonObject
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
            return AvoidReconciliation(receipt, pending.ToolId);
        }
        catch (SubagentControlRejectedException error)
        { return BrokerGatewayContracts.FailedReceipt(execution, error.Code); }
        catch (OperationCanceledException)
        {
            return new(execution.ExecutionId, execution.ToolCallId, ToolExecutionOutcome.Failed,
                "subagent_control_canceled", null, entered && pending.AccessKind != ToolAccessKind.ReadOnly,
                providerCallId: pending.Proposal.ProviderCallId);
        }
        catch (AgentProtocolException error) when (!entered)
        { return BrokerGatewayContracts.FailedReceipt(execution, error.Code); }
        catch (RuAgent.Harness.Persistence.HarnessStoreException error)
            when (SubagentControlFailures.IsDeterministic(error.Code))
        {
            // The refusal was decided before/inside a transaction; no effect can
            // have committed. Report a verified failure instead of pausing the run
            // with an unknown mutation outcome.
            return BrokerGatewayContracts.FailedReceipt(execution, error.Code);
        }
        catch (SubagentConfigurationException error)
            when (SubagentControlFailures.IsDeterministic(error.Code))
        {
            return BrokerGatewayContracts.FailedReceipt(execution, error.Code);
        }
        catch (ToolTransportException error)
            when (pending.ToolId == "agent_apply"
                && error.Message is "subagent_apply_root_not_open" or "subagent_apply_unavailable")
        {
            // The apply adapter refused before taking the journal; this is not an
            // indeterminate write. Let the model report/retry through a clean tool
            // failure rather than failing the root run.
            return BrokerGatewayContracts.FailedReceipt(execution, error.Message);
        }
        catch (AgentProtocolException error)
            when (pending.ToolId == "agent_apply"
                && error.Code is "subagent_apply_denied" or "subagent_apply_root_not_open" or "subagent_apply_unavailable")
        {
            return BrokerGatewayContracts.FailedReceipt(execution, error.Code);
        }
        catch (InvalidOperationException error) when (error.Message == "subagent_cancel_incomplete")
        {
            // Cancellation may have persisted before cleanup failed. Surface the
            // uncertainty as a failed control result; the model can inspect before
            // deciding, and the run does not park for a user Continue.
            return new(execution.ExecutionId, execution.ToolCallId, ToolExecutionOutcome.Failed,
                "subagent_cancel_incomplete", null, true, providerCallId: pending.Proposal.ProviderCallId);
        }
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
            return new(execution.ExecutionId, execution.ToolCallId, ToolExecutionOutcome.Failed,
                code, null, effects, providerCallId: pending.Proposal.ProviderCallId);
        }
    }

    /// <summary>
    /// Subagent controls are orchestration mutations, not arbitrary workspace writes.
    /// A durable control either committed or not; recording the uncertainty as a
    /// failed control result lets the model inspect and decide instead of parking the
    /// whole root run for a user Continue. Apply/cancel partial writes still carry
    /// <c>sideEffectsPossible=true</c>.
    /// </summary>
    private static ToolExecutionReceipt AvoidReconciliation(ToolExecutionReceipt receipt, string toolId)
    {
        if (!SubagentContractValidator.IsControlTool(toolId) && toolId != "agent_apply") return receipt;
        if (receipt.Outcome != ToolExecutionOutcome.OutcomeUnknown
            && !(receipt.SideEffectsPossible
                && receipt.Outcome is ToolExecutionOutcome.Cancelled or ToolExecutionOutcome.TimedOut))
        {
            return receipt;
        }

        return new(receipt.ExecutionId, receipt.ToolCallId, ToolExecutionOutcome.Failed,
            receipt.Code, receipt.ResultPayloadId, receipt.SideEffectsPossible,
            receipt.ModelContent, receipt.ContentEncoding, receipt.ProviderCallId);
    }
}
