using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

/// <summary>
/// IToolGateway adapter that evaluates and executes tool calls inside the
/// isolated Broker process over the V2 wire flow: tool.evaluate →
/// tool.decision → tool.prepare → tool.prepared → tool.execute →
/// tool.result. One gateway instance owns one connection and one open run
/// lease; requests on it are serialized.
/// </summary>
public sealed class BrokerToolGateway : IToolGateway, IAsyncDisposable
{
    private readonly Uri _brokerUrl;
    private readonly string _bearer;
    private readonly string _sessionId;
    private readonly string _runId;
    private readonly Func<JsonObject> _runOpenPayloadFactory;
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly Dictionary<string, PendingExecution> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private long _requestSequence;

    private sealed record PendingExecution(
        string ToolId,
        string ToolVersion,
        ToolAccessKind AccessKind,
        string PreparationToken,
        string ArgumentsJson);

    public BrokerToolGateway(
        Uri brokerUrl,
        string bearer,
        string sessionId,
        string runId,
        Func<JsonObject> runOpenPayloadFactory)
    {
        ArgumentNullException.ThrowIfNull(brokerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(runOpenPayloadFactory);

        _brokerUrl = brokerUrl;
        _bearer = bearer;
        _sessionId = sessionId;
        _runId = runId;
        _runOpenPayloadFactory = runOpenPayloadFactory;
    }

    public async Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var decisionReply = await RpcAsync(
            AgentMessageTypes.ToolEvaluate,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["tool_id"] = proposal.ToolId,
                ["tool_version"] = proposal.ToolVersion,
                ["tool_call_id"] = proposal.ToolCallId.Value,
                ["arguments"] = ParseArguments(proposal.Arguments),
            },
            cancellationToken).ConfigureAwait(false);

        var accessKind = AccessKindForTool(proposal.ToolId);
        JsonObject body;
        try
        {
            body = ThrowIfTransportError(decisionReply);
        }
        catch (ToolTransportException error) when (error.Message == "phase_not_available")
        {
            return ToolProposalDecision.Deny(accessKind, "phase_not_available");
        }

        return body["decision"]?.GetValue<string>() switch
        {
            "allow" => ToolProposalDecision.Allow(
                accessKind,
                body["decision_token"]?.GetValue<string>()
                    ?? throw new ToolTransportException("missing_decision_token")),
            "deny" => ToolProposalDecision.Deny(
                accessKind,
                body["reason_code"]?.GetValue<string>() ?? "policy_denied"),
            "ask" => ToolProposalDecision.Ask(
                accessKind,
                InternalApprovalId(body["approval_id"]?.GetValue<string>() ?? string.Empty),
                body["reason_code"]?.GetValue<string>() == "external_directory_required"
                    ? "external_directory_required"
                    : "approval_required"),
            _ => throw new ToolTransportException("invalid_decision"),
        };
    }

    public async Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var prepared = await RpcAsync(
            AgentMessageTypes.ToolPrepare,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["tool_id"] = execution.Proposal.ToolId,
                ["decision_token"] = execution.CanonicalIntentToken,
            },
            cancellationToken).ConfigureAwait(false);

        var body = ThrowIfTransportError(prepared);
        var token = body["preparation_token"]?.GetValue<string>()
            ?? throw new ToolTransportException("missing_preparation_token");
        var accessKind = AccessKindForTool(execution.Proposal.ToolId);
        _pending[execution.ExecutionId.Value] = new PendingExecution(
            execution.Proposal.ToolId,
            execution.Proposal.ToolVersion,
            accessKind,
            token,
            execution.Proposal.Arguments.GetRawText());
        return new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            accessKind,
            token,
            []);
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(publishDelta);

        if (!_pending.Remove(execution.ExecutionId.Value, out var pending))
        {
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
                sideEffectsPossible: pending.AccessKind != ToolAccessKind.ReadOnly);
        }

        var result = await RpcAsync(
            AgentMessageTypes.ToolExecute,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["tool_id"] = pending.ToolId,
                ["tool_version"] = pending.ToolVersion,
                ["preparation_token"] = pending.PreparationToken,
                ["arguments"] = ParseArgumentsJson(pending.ArgumentsJson),
            },
            cancellationToken).ConfigureAwait(false);

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
                        : "tool_failed");
        }
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancelled.Add(executionId.Value);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { } socket)
        {
            socket.Dispose();
            _socket = null;
        }

        _wire.Dispose();
    }

    private async Task<AgentEnvelope> RpcAsync(
        string messageType,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var requestId = $"gw-{Interlocked.Increment(ref _requestSequence)}";
            await SendAsync(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2,
                requestId,
                messageType,
                _sessionId,
                null,
                10_000,
                payload), cancellationToken).ConfigureAwait(false);
            return await ReceiveReplyAsync(requestId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _wire.Release();
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null)
        {
            return;
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _bearer);
        await socket.ConnectAsync(_brokerUrl, cancellationToken).ConfigureAwait(false);
        _socket = socket;
        var opened = await RoundTripAsync(
            AgentMessageTypes.RunOpen,
            _runOpenPayloadFactory(),
            cancellationToken).ConfigureAwait(false);
        if (opened.MessageType != AgentMessageTypes.RunOpened)
        {
            _socket = null;
            socket.Dispose();
            throw new ToolTransportException(
                opened.Payload["error_code"]?.GetValue<string>() ?? "run_not_open");
        }
    }

    private async Task<AgentEnvelope> RoundTripAsync(
        string messageType,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var requestId = $"gw-{Interlocked.Increment(ref _requestSequence)}";
        await SendAsync(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2,
            requestId,
            messageType,
            _sessionId,
            null,
            10_000,
            payload), cancellationToken).ConfigureAwait(false);
        return await ReceiveReplyAsync(requestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await (_socket ?? throw new ToolTransportException("broker_connection_closed"))
            .SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentEnvelope> ReceiveReplyAsync(string requestId, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await (_socket ?? throw new ToolTransportException("broker_connection_closed"))
                .ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new ToolTransportException("broker_connection_closed");
            }

            var reply = AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (reply.RequestId == requestId)
            {
                return reply;
            }
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

    private static ToolExecutionReceipt FailedReceipt(PreparedToolExecution execution, string code)
    {
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: Encoding.UTF8.GetBytes(
                new JsonObject { ["error"] = code }.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    private static ToolAccessKind AccessKindForTool(string toolId) => toolId switch
    {
        "files.write" or "files.edit" or "files.delete" or "files.restore" => ToolAccessKind.Mutation,
        "terminal.run" or "terminal.shell" => ToolAccessKind.Terminal,
        _ => ToolAccessKind.ReadOnly,
    };

    private static ApprovalId InternalApprovalId(string brokerApprovalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(brokerApprovalId));
        return new ApprovalId("apr_" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant());
    }

    private static JsonObject ParseArguments(JsonElement arguments) =>
        JsonNode.Parse(arguments.GetRawText())!.AsObject();

    private static JsonObject ParseArgumentsJson(string json) =>
        JsonNode.Parse(json)!.AsObject();
}
