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
/// isolated Broker process over the single-token wire flow: tool.evaluate →
/// tool.decision (allow carries one authorization_token, ask carries one
/// approval_id) → approval.respond → tool.execute → tool.result. One gateway
/// instance owns one connection and one open run lease; requests on it are
/// serialized.
/// </summary>
public sealed class BrokerToolGateway : IToolGateway, IAsyncDisposable
{
    internal const string AuthorizationTokenPrefix = "auth:";

    private readonly Uri _brokerUrl;
    private readonly string _bearer;
    private readonly string _sessionId;
    private readonly string _runId;
    private readonly Func<JsonObject> _runOpenPayloadFactory;
    private readonly BrokerApprovalState? _approvals;
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly Dictionary<string, PendingExecution> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private long _requestSequence;

    private sealed record PendingExecution(
        string ToolId,
        string ToolVersion,
        ToolAccessKind AccessKind,
        string AuthorizationToken,
        string ArgumentsJson);

    public BrokerToolGateway(
        Uri brokerUrl,
        string bearer,
        string sessionId,
        string runId,
        Func<JsonObject> runOpenPayloadFactory,
        BrokerApprovalState? approvals = null)
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
        _approvals = approvals;
    }

    public async Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var proposalArgs = ParseArguments(proposal.Arguments);
        var accessKind = AccessKindForTool(proposal.ToolId, proposalArgs);
        var argsKey = ApprovalArgsKey(proposal.ToolId, proposal.Arguments);
        if (_approvals is not null
            && _approvals.TryTakeAuthorizationToken(_runId, argsKey, out var storedToken)
            && storedToken is not null)
        {
            return ToolProposalDecision.Allow(
                accessKind,
                AuthorizationTokenPrefix + storedToken);
        }

        var decisionReply = await RpcAsync(
            AgentMessageTypes.ToolEvaluate,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["tool_id"] = proposal.ToolId,
                ["tool_version"] = proposal.ToolVersion,
                ["tool_call_id"] = proposal.ToolCallId.Value,
                ["arguments"] = (JsonNode)proposalArgs.DeepClone(),
            },
            cancellationToken).ConfigureAwait(false);

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
                body["authorization_token"]?.GetValue<string>()
                    ?? throw new ToolTransportException("missing_decision_token")),
            "deny" => ToolProposalDecision.Deny(
                accessKind,
                body["reason_code"]?.GetValue<string>() ?? "policy_denied",
                body["safe_summary"]?.GetValue<string>()),
            "ask" => RegisterBrokerAsk(
                proposal,
                argsKey,
                body["approval_id"]?.GetValue<string>()
                    ?? throw new ToolTransportException("missing_approval_id"),
                accessKind,
                body["reason_code"]?.GetValue<string>()),
            _ => throw new ToolTransportException("invalid_decision"),
        };
    }

    private ToolProposalDecision RegisterBrokerAsk(
        ToolProposal proposal,
        string argsKey,
        string brokerApprovalId,
        ToolAccessKind accessKind,
        string? reasonCode)
    {
        var hostApprovalId = InternalApprovalId(brokerApprovalId);
        _approvals?.RegisterAsk(
            _runId,
            hostApprovalId.Value,
            brokerApprovalId,
            argsKey);
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
                "web_approval_required" => "web_approval_required",
                _ => "approval_required",
            });
    }

    public async Task<PreparedToolExecution> PrepareAsync(
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

        _pending[execution.ExecutionId.Value] = new PendingExecution(
            execution.Proposal.ToolId,
            execution.Proposal.ToolVersion,
            accessKind,
            authorizationToken,
            execution.Proposal.Arguments.GetRawText());
        return new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            accessKind,
            authorizationToken,
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
            // A cancelled execution never reached PrepareAsync, so its id can still be
            // sitting in _cancelled. Drop it here instead of leaving it for the life of
            // the run.
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
                sideEffectsPossible: pending.AccessKind != ToolAccessKind.ReadOnly);
        }

        var result = await RpcAsync(
            AgentMessageTypes.ToolExecute,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["tool_id"] = pending.ToolId,
                ["tool_version"] = pending.ToolVersion,
                ["authorization_token"] = pending.AuthorizationToken,
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

    /// Forwards an approval resolution to the Broker on this gateway's
    /// connection (whose session owns the run lease). On success the returned
    /// one-time authorization token is stored under the original intent key so
    /// the resumed proposal evaluates and executes without a new ask.
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string brokerApprovalId,
        string command,
        string argsKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);

        var reply = await RpcAsync(
            AgentMessageTypes.ApprovalRespond,
            new JsonObject
            {
                ["run_id"] = _runId,
                ["approval_id"] = brokerApprovalId,
                ["command"] = command,
            },
            cancellationToken).ConfigureAwait(false);

        if (reply.MessageType != AgentMessageTypes.ApprovalResult
            || reply.Payload["status"]?.GetValue<string>() != "authorized")
        {
            return (false, null);
        }

        var token = reply.Payload["authorization_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        _approvals?.StoreAuthorizationToken(
            _runId,
            argsKey,
            token,
            DateTimeOffset.UtcNow.AddSeconds(60));
        return (true, token);
    }

    /// <summary>
    /// Revokes one Agent session's directory grants in the Broker process.
    /// Returns false only when a live Broker explicitly denies; transport
    /// failures propagate so the caller can treat Broker-unavailable as safe.
    /// </summary>
    public async Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var reply = await RpcAsync(
            AgentMessageTypes.SessionGrantsRevoke,
            new JsonObject { ["agent_session_id"] = sessionId },
            cancellationToken).ConfigureAwait(false);
        return reply.MessageType == AgentMessageTypes.SessionGrantsRevoked
            && string.Equals(
                reply.Payload["status"]?.GetValue<string>(),
                ToolResultStatuses.Succeeded,
                StringComparison.Ordinal);
    }

    private static string ApprovalArgsKey(string toolId, JsonElement arguments)
    {
        var canonicalArguments = CanonicalizeJson(JsonNode.Parse(arguments.GetRawText())!);
        return toolId + "|" + canonicalArguments.ToJsonString();
    }

    private static JsonNode CanonicalizeJson(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, CanonicalizeJson(item.Value!)))),
        JsonArray array => new JsonArray(array.Select(item => (JsonNode?)CanonicalizeJson(item!)).ToArray()),
        _ => node.DeepClone(),
    };

    public async ValueTask DisposeAsync()
    {
        InvalidateConnection();
        _pending.Clear();
        _cancelled.Clear();
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
        catch (Exception error) when (error is ToolTransportException or WebSocketException or IOException)
        {
            // A broken socket used to stay in _socket, so EnsureReadyAsync never tried again
            // and every later call on this gateway failed with broker_connection_closed for
            // the rest of the run. Drop it so the next call reconnects and re-opens the run
            // lease, which the Broker treats as an idempotent publish of the same lease
            // (RunOpen re-assigns _runLeases[run_id] after checking the snapshot hash and
            // advertised capabilities, so the policy in force cannot change underneath).
            InvalidateConnection();
            throw;
        }
        finally
        {
            _wire.Release();
        }
    }

    private void InvalidateConnection()
    {
        var socket = _socket;
        _socket = null;
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Dispose();
        }
        catch (Exception)
        {
            // Disposal of an already-faulted socket must not replace the transport error
            // that explains what actually happened.
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

    private const int MaxBrokerFrameBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private async Task<AgentEnvelope> ReceiveReplyAsync(string requestId, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new ToolTransportException("broker_connection_closed");
        using var content = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var result = await socket
                .ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new ToolTransportException("broker_connection_closed");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ToolTransportException("broker_unexpected_frame");
            }

            if (content.Length + result.Count > MaxBrokerFrameBytes)
            {
                throw new ToolTransportException("broker_frame_too_large");
            }

            content.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(content.GetBuffer(), 0, (int)content.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new ToolTransportException("broker_invalid_utf8");
            }

            content.SetLength(0);
            var reply = AgentEnvelopeCodec.Parse(text);
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

    private static ToolAccessKind AccessKindForTool(string toolId, JsonObject? arguments = null) => toolId switch
    {
        "write" or "edit" => ToolAccessKind.Mutation,
        "bash" or "web_search" or "web_fetch" => ToolAccessKind.Terminal,
        "read" or "grep" or "glob" => ToolAccessKind.ReadOnly,
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
