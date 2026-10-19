using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGateway
{
    public async Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var native = await EvaluateNativeSubagentAsync(proposal, cancellationToken).ConfigureAwait(false);
        if (native is not null) return native;
        var proposalArgs = BrokerGatewayContracts.ParseArguments(proposal.Arguments);
        var accessKind = BrokerGatewayContracts.AccessKindForTool(proposal.ToolId, proposalArgs);
        var argsKey = BrokerGatewayContracts.ProposalApprovalArgsKey(proposal);

        BrokerRunLeaseIdentity lease;
        long generation;
        lock (_sync)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(InProcessToolGateway));
            }

            lease = _lease;
            generation = _generation;
        }

        // A token cached from a resolved approval authorizes the resumed
        // proposal without re-evaluating: the authority consumed the pending
        // approval already, so a second evaluate would only ask again.
        if (_approvals is not null
            && _approvals.TryTakeAuthorizationToken(_runId, lease, argsKey, out var storedToken)
            && storedToken is not null)
        {
            return ToolProposalDecision.Allow(
                accessKind,
                BrokerGatewayContracts.AuthorizationTokenPrefix + storedToken,
                generation);
        }

        var result = await _runtime
            .EvaluateAsync(
                Request(lease.SessionId, AgentMessageTypes.ToolEvaluate, new JsonObject
                {
                    ["run_id"] = _runId,
                    ["tool_id"] = proposal.ToolId,
                    ["tool_version"] = proposal.ToolVersion,
                    ["tool_call_id"] = proposal.ToolCallId.Value,
                    ["arguments"] = proposalArgs.DeepClone(),
                }),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            // The wire gateway turns this one refusal into a deny (it is a
            // capability-boundary answer, not a transport failure) and lets
            // every other authority error surface as a transport failure.
            if (string.Equals(result.Error.ErrorCode, "phase_not_available", StringComparison.Ordinal))
            {
                return ToolProposalDecision.Deny(
                    accessKind,
                    "phase_not_available",
                    gatewayGeneration: generation);
            }

            throw new ToolTransportException(result.Error.ErrorCode);
        }

        var decision = result.Decision
            ?? throw new ToolTransportException("invalid_decision");
        return decision.Decision switch
        {
            "allow" => ToolProposalDecision.Allow(
                accessKind,
                decision.AuthorizationToken
                    ?? throw new ToolTransportException("missing_decision_token"),
                generation),
            "deny" => ToolProposalDecision.Deny(
                accessKind,
                decision.ReasonCode ?? "policy_denied",
                decision.SafeSummary,
                generation),
            "ask" => RegisterAsk(
                proposal,
                lease,
                argsKey,
                decision.ApprovalId
                    ?? throw new ToolTransportException("missing_approval_id"),
                accessKind,
                decision.ReasonCode,
                generation),
            _ => throw new ToolTransportException("invalid_decision"),
        };
    }

    private ToolProposalDecision RegisterAsk(
        ToolProposal proposal,
        BrokerRunLeaseIdentity lease,
        string argsKey,
        string brokerApprovalId,
        ToolAccessKind accessKind,
        string? reasonCode,
        long generation)
    {
        var hostApprovalId = BrokerGatewayContracts.InternalApprovalId(brokerApprovalId);
        if (_approvals is not null
            && !_approvals.RegisterAsk(
                proposal.RunId.Value,
                lease,
                hostApprovalId.Value,
                brokerApprovalId,
                argsKey))
        {
            throw new ToolTransportException("broker_lease_generation_changed");
        }

        return ToolProposalDecision.Ask(
            accessKind,
            hostApprovalId,
            BrokerGatewayContracts.MapAskReasonCode(reasonCode),
            gatewayGeneration: generation);
    }

    public Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        var preparedArgs = BrokerGatewayContracts.ParseArguments(execution.Proposal.Arguments);
        var accessKind = BrokerGatewayContracts.AccessKindForTool(execution.Proposal.ToolId, preparedArgs);
        var canonicalToken = execution.CanonicalIntentToken;
        var authorizationToken = canonicalToken.StartsWith(
                BrokerGatewayContracts.AuthorizationTokenPrefix,
                StringComparison.Ordinal)
            ? canonicalToken[BrokerGatewayContracts.AuthorizationTokenPrefix.Length..]
            : canonicalToken;

        long generation;
        lock (_sync)
        {
            if (IsDisposed || execution.GatewayGeneration != _generation)
            {
                throw new ToolTransportException("broker_lease_generation_changed");
            }

            generation = _generation;
            _pending[execution.ExecutionId.Value] = new PendingExecution(
                generation,
                execution.Proposal.ToolId,
                execution.Proposal.ToolVersion,
                accessKind,
                authorizationToken,
                execution.Proposal.Arguments.GetRawText(),
                execution.Proposal);
        }

        return Task.FromResult(new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            accessKind,
            authorizationToken,
            [],
            generation));
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(publishDelta);

        PendingExecution pending;
        string sessionId;
        lock (_sync)
        {
            if (!_pending.Remove(execution.ExecutionId.Value, out var found))
            {
                _cancelled.Remove(execution.ExecutionId.Value);
                return BrokerGatewayContracts.FailedReceipt(execution, "decision_stale");
            }

            if (_cancelled.Remove(execution.ExecutionId.Value))
            {
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Cancelled,
                    "cancelled",
                    null,
                    sideEffectsPossible: found.AccessKind != ToolAccessKind.ReadOnly);
            }

            if (execution.GatewayGeneration != found.Generation
                || _generation != found.Generation)
            {
                return BrokerGatewayContracts.FailedReceipt(execution, "decision_stale");
            }

            pending = found;
            sessionId = _lease.SessionId;
        }

        if (RuAgent.Protocol.Subagents.SubagentContractValidator.IsControlTool(pending.ToolId))
            return await ExecuteNativeSubagentAsync(pending, execution, sessionId, cancellationToken).ConfigureAwait(false);
        if (pending.ToolId == "agent_apply")
            return _subagents is null ? BrokerGatewayContracts.FailedReceipt(execution, "phase_not_available")
                : await _subagents.Value.ApplyAsync(pending.Proposal, execution, cancellationToken).ConfigureAwait(false);

        var reply = await _runtime
            .ExecuteAuthorizedAsync(
                Request(sessionId, AgentMessageTypes.ToolExecute, new JsonObject
                {
                    ["run_id"] = _runId,
                    ["tool_id"] = pending.ToolId,
                    ["tool_version"] = pending.ToolVersion,
                    ["authorization_token"] = pending.AuthorizationToken,
                    ["arguments"] = BrokerGatewayContracts.ParseArgumentsJson(pending.ArgumentsJson),
                }),
                cancellationToken)
            .ConfigureAwait(false);

        if (reply.Receipt is null)
        {
            return BrokerGatewayContracts.FailedReceipt(
                execution,
                reply.ErrorCode is { Length: > 0 } errorCode ? errorCode : "tool_failed",
                reply.SafeSummary);
        }

        var result = reply.Receipt;
        switch (result.Status)
        {
            case ToolResultStatuses.Succeeded:
                return BrokerGatewayContracts.SucceededReceipt(execution, result.Output, pending.AccessKind);
            case ToolResultStatuses.Cancelled:
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Cancelled,
                    "cancelled",
                    null,
                    sideEffectsPossible: pending.AccessKind != ToolAccessKind.ReadOnly);
            default:
                return BrokerGatewayContracts.FailedReceipt(
                    execution,
                    result.ErrorCode is { Length: > 0 } failedCode ? failedCode : "tool_failed",
                    result.SafeSummary,
                    result.Output);
        }
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_pending.ContainsKey(executionId.Value))
            {
                _cancelled.Add(executionId.Value);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Forwards run-scoped cancellation to the runtime so an in-flight terminal
    /// execution terminates its whole process tree through the Job Object.
    ///
    /// Best effort by contract, exactly like the wire gateway: the caller
    /// commits the durable cancelled projection straight after this returns, so
    /// anything that escapes here would fail the cancel request and leave the
    /// run running *and* uncancelled. The runtime keys the cancel by run id and
    /// reports how many executions it stopped, so the run actually asked for is
    /// the one cancelled.
    /// </summary>
    public Task CancelRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        try
        {
            _runtime.CancelRun(runId.Value);
        }
        catch (Exception error) when (error is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // A disposed runtime or a racing run close needs no cancel from
            // here: the durable cancel is what the caller actually promised.
        }

        return Task.CompletedTask;
    }
}
