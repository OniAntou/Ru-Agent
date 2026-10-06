using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Broker;

public sealed partial class BrokerToolGateway
{
    public async Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var proposalArgs = ParseArguments(proposal.Arguments);
        var accessKind = AccessKindForTool(proposal.ToolId, proposalArgs);
        var argsKey = ApprovalArgsKey(proposal.ToolId, proposal.Arguments);
        return await EvaluateOnBrokerAsync(
                proposal,
                proposalArgs,
                argsKey,
                accessKind,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ToolProposalDecision RegisterBrokerAsk(
        ToolProposal proposal,
        BrokerRunLeaseIdentity? responseLease,
        string argsKey,
        string brokerApprovalId,
        ToolAccessKind accessKind,
        string? reasonCode,
        long gatewayGeneration)
    {
        var hostApprovalId = InternalApprovalId(brokerApprovalId);
        if (_approvals is not null
            && (responseLease is null
                || !_approvals.RegisterAsk(
                    proposal.RunId.Value,
                    responseLease,
                    hostApprovalId.Value,
                    brokerApprovalId,
                    argsKey)))
        {
            throw new ToolTransportException("broker_lease_generation_changed");
        }

        return ToolProposalDecision.Ask(
            accessKind,
            hostApprovalId,
            reasonCode switch
            {
                "external_directory_required" => "external_directory_required",
                "terminal_delete_requires_approval" => "terminal_delete_requires_approval",
                "terminal_external_mutation_requires_approval" => "terminal_external_mutation_requires_approval",
                "terminal_timeout_requires_approval" => "terminal_timeout_requires_approval",
                "terminal_unknown_executable" => "terminal_unknown_executable",
                "terminal_protected_path_requires_approval" => "terminal_protected_path_requires_approval",
                "web_approval_required" => "web_approval_required",
                _ => "approval_required",
            },
            gatewayGeneration: gatewayGeneration);
    }

    private async Task<ToolProposalDecision> EvaluateOnBrokerAsync(
        ToolProposal proposal,
        JsonObject proposalArgs,
        string argsKey,
        ToolAccessKind accessKind,
        CancellationToken cancellationToken)
    {
        GatewayBinding? binding = null;
        ClientWebSocket? socket = null;
        GatewayGeneration? connectionGeneration = null;
        try
        {
            binding = GetBinding();
            await EnsureReadyForEvaluationAsync(binding, cancellationToken).ConfigureAwait(false);
            var (lease, generation) = GetCurrentState(binding);
            if (generation is null)
            {
                throw new ToolTransportException("broker_lease_generation_changed");
            }

            connectionGeneration = generation;
            lock (_bindingSync)
            {
                socket = ReferenceEquals(_binding, binding) ? _socket : null;
            }

            if (_approvals is not null
                && lease is not null
                && _approvals.TryTakeAuthorizationToken(
                    binding.RunId,
                    lease,
                    argsKey,
                    out var storedToken)
                && storedToken is not null)
            {
                return ToolProposalDecision.Allow(
                    accessKind,
                    AuthorizationTokenPrefix + storedToken,
                    generation.Value);
            }

            var decisionReply = await RoundTripAsync(
                    binding,
                    AgentMessageTypes.ToolEvaluate,
                    new JsonObject
                    {
                        ["run_id"] = binding.RunId,
                        ["tool_id"] = proposal.ToolId,
                        ["tool_version"] = proposal.ToolVersion,
                        ["tool_call_id"] = proposal.ToolCallId.Value,
                        ["arguments"] = (JsonNode)proposalArgs.DeepClone(),
                    },
                    cancellationToken,
                    lease,
                    generation)
                .ConfigureAwait(false);
            JsonObject body;
            try
            {
                body = ThrowIfTransportError(decisionReply);
            }
            catch (ToolTransportException error) when (error.Message == "phase_not_available")
            {
                return ToolProposalDecision.Deny(
                    accessKind,
                    "phase_not_available",
                    gatewayGeneration: generation.Value);
            }

            return body["decision"]?.GetValue<string>() switch
            {
                "allow" => ToolProposalDecision.Allow(
                    accessKind,
                    body["authorization_token"]?.GetValue<string>()
                        ?? throw new ToolTransportException("missing_decision_token"),
                    generation.Value),
                "deny" => ToolProposalDecision.Deny(
                    accessKind,
                    body["reason_code"]?.GetValue<string>() ?? "policy_denied",
                    body["safe_summary"]?.GetValue<string>(),
                    generation.Value),
                "ask" => RegisterBrokerAsk(
                    proposal,
                    lease,
                    argsKey,
                    body["approval_id"]?.GetValue<string>()
                        ?? throw new ToolTransportException("missing_approval_id"),
                    accessKind,
                    body["reason_code"]?.GetValue<string>(),
                    generation.Value),
                _ => throw new ToolTransportException("invalid_decision"),
            };
        }
        catch (Exception error) when (error is
            ToolTransportException
            or WebSocketException
            or IOException
            or SocketException
            or ObjectDisposedException)
        {
            if (socket is not null)
            {
                InvalidateConnection(binding, socket, connectionGeneration?.Value);
            }

            throw;
        }
    }

    public Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        var preparedArgs = ParseArguments(execution.Proposal.Arguments);
        var accessKind = AccessKindForTool(execution.Proposal.ToolId, preparedArgs);
        var canonicalToken = execution.CanonicalIntentToken;
        var authorizationToken = canonicalToken.StartsWith(AuthorizationTokenPrefix, StringComparison.Ordinal)
            ? canonicalToken[AuthorizationTokenPrefix.Length..]
            : canonicalToken;

        GatewayGeneration generation;
        lock (_bindingSync)
        {
            if (IsDisposed
                || _currentGeneration is not { } currentGeneration
                || execution.GatewayGeneration != currentGeneration.Value)
            {
                throw new ToolTransportException("broker_lease_generation_changed");
            }

            generation = currentGeneration;
            _pending[execution.ExecutionId.Value] = new PendingExecution(
                generation,
                execution.Proposal.ToolId,
                execution.Proposal.ToolVersion,
                accessKind,
                authorizationToken,
                execution.Proposal.Arguments.GetRawText());
        }

        return Task.FromResult(new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            accessKind,
            authorizationToken,
            [],
            generation.Value));
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(publishDelta);

        PendingExecution pending;
        lock (_bindingSync)
        {
            if (!_pending.Remove(execution.ExecutionId.Value, out var found))
            {
                _cancelled.Remove(execution.ExecutionId.Value);
                return FailedReceipt(execution, "decision_stale");
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

            if (execution.GatewayGeneration != found.Generation.Value
                || _currentGeneration != found.Generation)
            {
                return FailedReceipt(execution, "decision_stale");
            }

            pending = found;
        }

        if (BeforeExecutionSendAsync is { } beforeExecutionSend)
        {
            await beforeExecutionSend(pending.Generation.Lease, cancellationToken).ConfigureAwait(false);
        }

        var dispatch = new ExecutionDispatchState();
        try
        {
            var result = await RpcAsync(
                    AgentMessageTypes.ToolExecute,
                    binding => new JsonObject
                    {
                        ["run_id"] = binding.RunId,
                        ["tool_id"] = pending.ToolId,
                        ["tool_version"] = pending.ToolVersion,
                        ["authorization_token"] = pending.AuthorizationToken,
                        ["arguments"] = ParseArgumentsJson(pending.ArgumentsJson),
                    },
                    cancellationToken,
                    expectedGeneration: pending.Generation,
                    onSendStarted: dispatch.MarkDispatched,
                    replyTextTransform: ExecutionReplyTextTransform)
                .ConfigureAwait(false);
            var body = ThrowIfTransportError(result);
            var status = body["status"]?.GetValue<string>() ?? ToolResultStatuses.Failed;
            switch (status)
            {
                case ToolResultStatuses.Succeeded:
                    return SucceededReceipt(execution, body["output"] as JsonObject ?? new JsonObject(), pending.AccessKind);
                case ToolResultStatuses.Cancelled:
                    return new ToolExecutionReceipt(
                        execution.ExecutionId,
                        execution.ToolCallId,
                        ToolExecutionOutcome.Cancelled,
                        "cancelled",
                        null,
                        sideEffectsPossible: pending.AccessKind != ToolAccessKind.ReadOnly);
                default:
                    return FailedReceipt(
                        execution,
                        body["error_code"]?.GetValue<string>() is { Length: > 0 } errorCode
                            ? errorCode
                            : "tool_failed",
                        body["safe_summary"]?.GetValue<string>(),
                        body["output"] as JsonObject);
            }
        }
        catch (ToolTransportException error) when (
            !dispatch.IsDispatched
            && error.Message is "broker_lease_generation_changed" or "gateway_binding_changed")
        {
            return FailedReceipt(execution, "decision_stale");
        }
        catch (Exception error) when (IsPostDispatchExecuteFailure(
            dispatch,
            pending.AccessKind,
            error))
        {
            return TransportFailureReceipt(execution);
        }
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingSync)
        {
            if (_pending.ContainsKey(executionId.Value))
            {
                _cancelled.Add(executionId.Value);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Forwards run-scoped cancellation to the Broker so an in-flight terminal
    /// execution terminates its whole process tree through the Job Object.
    ///
    /// Best effort by contract: the caller commits the durable cancelled
    /// projection straight after this returns, so anything that escapes here
    /// would fail the cancel request and leave the run running *and* uncancelled.
    /// The Broker keys the cancel by the run id in the payload and reports how
    /// many executions it stopped, so the run actually asked for is the one sent.
    /// </summary>
    public async Task CancelRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        try
        {
            await RpcAsync(
                AgentMessageTypes.Cancel,
                _ => new JsonObject { ["run_id"] = runId.Value },
                static (reply, _) => reply,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // A closed socket needs no cancel: the Broker cancels its
            // per-connection dispatch token on close, so the linked run token is
            // already cancelled and the runner is unwinding on its own. Any other
            // transport failure is equally non-actionable here -- the durable
            // cancel is what the caller actually promised the user.
        }
    }

    private static JsonObject ThrowIfTransportError(AgentEnvelope reply)
    {
        if (reply.MessageType == AgentMessageTypes.ToolResult
            && reply.Payload["status"]?.GetValue<string>() == ToolResultStatuses.Denied)
        {
            throw new ToolTransportException(
                reply.Payload["error_code"]?.GetValue<string>() ?? "broker_error");
        }

        return reply.Payload;
    }

    private static ToolExecutionReceipt SucceededReceipt(
        PreparedToolExecution execution,
        JsonObject output,
        ToolAccessKind accessKind)
    {
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Succeeded,
            "ok",
            null,
            sideEffectsPossible: accessKind != ToolAccessKind.ReadOnly,
            modelContent: Encoding.UTF8.GetBytes(output.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    private static ToolExecutionReceipt FailedReceipt(
        PreparedToolExecution execution,
        string code,
        string? safeSummary = null,
        JsonObject? output = null)
    {
        var content = new JsonObject { ["error"] = code };
        if (BoundRepairHint(safeSummary) is { } hint)
        {
            content["hint"] = hint;
        }

        if (BoundSpoolId(output) is { } spoolId)
        {
            content["spool_id"] = spoolId;
        }

        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: Encoding.UTF8.GetBytes(content.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    internal const int MaximumRepairHintLength = 512;

    /// <summary>
    /// Bounds the Broker's repair summary before it becomes model-visible: one
    /// line, at most <see cref="MaximumRepairHintLength"/> characters, and never
    /// a forbidden field name. Serializer output escapes content quotes as
    /// <c>\u0022</c>, so both spellings are normalized before matching, exactly
    /// like the NDJSON privacy guard.
    /// </summary>
    internal static string? BoundRepairHint(string? safeSummary)
    {
        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            return null;
        }

        var cleaned = safeSummary.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Length == 0 || cleaned.Contains('\0'))
        {
            return null;
        }

        if (cleaned.Length > MaximumRepairHintLength)
        {
            cleaned = cleaned[..MaximumRepairHintLength];
        }

        var serialized = new JsonObject { ["hint"] = cleaned }.ToJsonString();
        var normalized = serialized
            .ToLowerInvariant()
            .Replace("\\u0022", "\"", StringComparison.Ordinal);
        return HarnessEventContractValidator.ContainsForbiddenField(normalized)
            ? null
            : cleaned;
    }

    /// <summary>
    /// Carries a terminal execution's retained-output identity into failure content
    /// so a failed command can still be inspected with mode read_output. Only the
    /// bounded identity crosses; the raw output never does.
    /// </summary>
    internal static string? BoundSpoolId(JsonObject? output)
    {
        if (output?["spool_id"] is JsonValue value
            && value.TryGetValue<string>(out var spoolId)
            && !string.IsNullOrWhiteSpace(spoolId)
            && spoolId.Length <= 256
            && !spoolId.Contains('\0'))
        {
            return spoolId;
        }

        return null;
    }

    private static bool IsPostDispatchExecuteFailure(
        ExecutionDispatchState dispatch,
        ToolAccessKind accessKind,
        Exception error) =>
        dispatch.IsDispatched
        && (accessKind != ToolAccessKind.ReadOnly || error is not OperationCanceledException)
        && error is (
            ToolTransportException
            or WebSocketException
            or IOException
            or SocketException
            or AgentProtocolException
            or JsonException
            or InvalidOperationException
            or FormatException
            or OperationCanceledException
            or ObjectDisposedException);

    private static ToolExecutionReceipt TransportFailureReceipt(PreparedToolExecution execution)
    {
        // Every requested call needs a bounded model-visible result; an empty content
        // leaves the assistant call unmatched and the next step fails as
        // model_history_invalid.
        var content = new JsonObject { ["error"] = "transport_failure" };
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            execution.AccessKind == ToolAccessKind.ReadOnly
                ? ToolExecutionOutcome.Failed
                : ToolExecutionOutcome.OutcomeUnknown,
            "transport_failure",
            null,
            sideEffectsPossible: execution.AccessKind != ToolAccessKind.ReadOnly,
            modelContent: Encoding.UTF8.GetBytes(content.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    internal static ToolAccessKind AccessKindForTool(string toolId, JsonObject? arguments = null) =>
        toolId switch
        {
            "write" or "edit" or "todo_write" => ToolAccessKind.Mutation,
            "bash" => ToolAccessKind.Terminal,
            _ when BrokerPolicyContracts.IsReadOnlyTool(toolId) => ToolAccessKind.ReadOnly,
            _ => ToolAccessKind.Terminal,
        };

    private static JsonObject ParseArguments(JsonElement arguments) =>
        JsonNode.Parse(arguments.GetRawText())!.AsObject();

    private static JsonObject ParseArgumentsJson(string json) =>
        JsonNode.Parse(json)!.AsObject();
}
