using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Broker;

public sealed class BrokerToolGateway : IToolGateway, IToolRunCancellation, IApprovalCleanupGateway, IAsyncDisposable
{
    internal const string AuthorizationTokenPrefix = "auth:";

    private readonly BrokerApprovalState? _approvals;
    private readonly object _bindingSync = new();
    private GatewayBinding _binding;
    private BrokerRunLeaseIdentity? _currentLease;
    private GatewayGeneration? _currentGeneration;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Dictionary<string, PendingExecution> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingReply> _pendingReplies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private CancellationTokenSource? _receiveCancellation;
    private long _requestSequence;
    private long _connectionGeneration;
    private readonly object _rpcSync = new();
    private int _activeRpcCount;
    private TaskCompletionSource<bool> _rpcsDrained = CompletedRpcSignal();
    private int _disposed;

    private sealed record GatewayBinding(
        Uri BrokerUrl,
        string Bearer,
        string SessionId,
        string RunId,
        Func<JsonObject> RunOpenPayloadFactory,
        BrokerRunLeaseIdentity? Lease);

    private sealed record GatewayGeneration(
        BrokerRunLeaseIdentity? Lease,
        long Value);

    private sealed class ExecutionDispatchState
    {
        public bool IsDispatched { get; private set; }

        public void MarkDispatched() => IsDispatched = true;
    }

    private sealed record PendingExecution(
        GatewayGeneration Generation,
        string ToolId,
        string ToolVersion,
        ToolAccessKind AccessKind,
        string AuthorizationToken,
        string ArgumentsJson);

    private sealed record PendingReply(
        TaskCompletionSource<AgentEnvelope> Completion,
        TaskCompletionSource<bool> SendHook,
        Func<string, string>? ReplyTextTransform);

    private readonly struct ActiveRpcScope : IDisposable
    {
        private readonly BrokerToolGateway _owner;

        public ActiveRpcScope(BrokerToolGateway owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner.ExitRpc();
    }

    public BrokerToolGateway(
        Uri brokerUrl,
        string bearer,
        string sessionId,
        string runId,
        Func<JsonObject> runOpenPayloadFactory,
        BrokerApprovalState? approvals = null,
        BrokerRunLeaseIdentity? lease = null)
    {
        ArgumentNullException.ThrowIfNull(brokerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(runOpenPayloadFactory);

        _approvals = approvals;
        if (approvals is not null && lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        _binding = new GatewayBinding(
            brokerUrl,
            bearer,
            sessionId,
            runId,
            runOpenPayloadFactory,
            lease);
    }

    public BrokerRunLeaseIdentity? Lease
    {
        get
        {
            lock (_bindingSync)
            {
                return _currentLease;
            }
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal Func<BrokerRunLeaseIdentity?, CancellationToken, Task>? BeforeExecutionSendAsync { get; set; }

    internal Func<CancellationToken, Task>? AfterExecutionSendAsync { get; set; }

    internal Func<string, string>? ExecutionReplyTextTransform { get; set; }

    internal Func<CancellationToken, Task>? ConnectionSetupFailureAsync { get; set; }

    internal void Rebind(
        Uri brokerUrl,
        string bearer,
        string sessionId,
        string runId,
        Func<JsonObject> runOpenPayloadFactory,
        BrokerRunLeaseIdentity lease)
    {
        ArgumentNullException.ThrowIfNull(brokerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(runOpenPayloadFactory);
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(runId, lease.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The lease does not belong to the run.", nameof(lease));
        }

        _sendGate.Wait();
        try
        {
            ClientWebSocket? socket;
            CancellationTokenSource? receiveCancellation;
            PendingReply[] pendingReplies;
            lock (_bindingSync)
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(BrokerToolGateway));
                }

                _binding = new GatewayBinding(
                    brokerUrl,
                    bearer,
                    sessionId,
                    runId,
                    runOpenPayloadFactory,
                    lease);
                _currentLease = null;
                _currentGeneration = null;
                Interlocked.Increment(ref _connectionGeneration);
                _pending.Clear();
                _cancelled.Clear();
                pendingReplies = _pendingReplies.Values.ToArray();
                _pendingReplies.Clear();
                receiveCancellation = _receiveCancellation;
                _receiveCancellation = null;
                _receiveLoop = null;
                socket = _socket;
                _socket = null;
                _approvals?.ActivateLease(lease);
            }

            receiveCancellation?.Cancel();
            CompletePending(pendingReplies, "broker_connection_closed");
            try
            {
                socket?.Dispose();
            }
            catch (Exception)
            {
            }
            finally
            {
                receiveCancellation?.Dispose();
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

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
                            : "tool_failed");
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

    /// Forwards an approval resolution to the Broker on this gateway's
    /// connection (whose session owns the run lease). On success the returned
    /// one-time authorization token is stored under the original intent key so
    /// the resumed proposal evaluates and executes without a new ask.
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string brokerApprovalId,
        string command,
        string argsKey,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        if (expectedLease is not null
            && GetCurrentLease(GetBinding()) != expectedLease)
        {
            return (false, null);
        }

        try
        {
            return await RpcAsync(
                    AgentMessageTypes.ApprovalRespond,
                    binding => new JsonObject
                    {
                        ["run_id"] = binding.RunId,
                        ["approval_id"] = brokerApprovalId,
                        ["command"] = command,
                    },
                    (reply, lease) =>
                    {
                        if (expectedLease is not null && lease != expectedLease)
                        {
                            return (false, (string?)null);
                        }

                        if (reply.MessageType != AgentMessageTypes.ApprovalResult
                            || reply.Payload["status"]?.GetValue<string>() != "authorized")
                        {
                            return (false, (string?)null);
                        }

                        var token = reply.Payload["authorization_token"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            return (false, (string?)null);
                        }

                        if (_approvals is not null
                            && (lease is null
                                || !_approvals.StoreAuthorizationToken(
                                    lease.RunId,
                                    lease,
                                    argsKey,
                                    token,
                                    DateTimeOffset.UtcNow.AddSeconds(60))))
                        {
                            return (false, (string?)null);
                        }

                        return (true, token);
                    },
                    cancellationToken,
                    expectedLease)
                .ConfigureAwait(false);
        }
        catch (ToolTransportException error) when (
            error.Message is "broker_lease_generation_changed" or "gateway_binding_changed")
        {
            return (false, null);
        }
    }

    public async Task<bool> RevokeApprovalAsync(
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var binding = GetBinding();
        var lease = GetCurrentLease(binding);
        if (lease is null
            || !_approvals!.TryGetAsk(
                binding.RunId,
                lease,
                approvalId.Value,
                out var brokerApprovalId,
                out _))
        {
            return false;
        }

        return await RpcAsync(
                AgentMessageTypes.ApprovalRespond,
                current => new JsonObject
                {
                    ["run_id"] = current.RunId,
                    ["approval_id"] = brokerApprovalId,
                    ["command"] = "deny",
                },
                (reply, responseLease) =>
                {
                    var revoked = responseLease == lease
                        && reply.MessageType == AgentMessageTypes.ApprovalResult
                        && string.Equals(
                            reply.Payload["status"]?.GetValue<string>(),
                            ToolResultStatuses.Denied,
                            StringComparison.Ordinal)
                        && string.Equals(
                            reply.Payload["error_code"]?.GetValue<string>(),
                            "approval_denied",
                            StringComparison.Ordinal);
                    if (revoked)
                    {
                        _approvals.RemoveAsk(binding.RunId, lease, approvalId.Value);
                    }

                    return revoked;
                },
                cancellationToken,
                lease)
            .ConfigureAwait(false);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? receiveLoop;
        CancellationTokenSource? receiveCancellation;
        TaskCompletionSource<bool> rpcsDrained;
        lock (_bindingSync)
        {
            receiveLoop = _receiveLoop;
            receiveCancellation = _receiveCancellation;
            _receiveLoop = null;
            _receiveCancellation = null;
        }

        lock (_rpcSync)
        {
            rpcsDrained = _rpcsDrained;
        }

        InvalidateConnection();
        receiveCancellation?.Cancel();
        try
        {
            await Task.WhenAll(
                    rpcsDrained.Task,
                    receiveLoop ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        receiveCancellation?.Dispose();
    }

    private async Task EnsureReadyForEvaluationAsync(
        GatewayBinding binding,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var wasConnected = IsConnected(binding);
            ClientWebSocket? socket = null;
            GatewayGeneration? connectionGeneration = null;
            try
            {
                await EnsureReadyAsync(binding, cancellationToken).ConfigureAwait(false);
                connectionGeneration = GetCurrentState(binding).Generation;
                lock (_bindingSync)
                {
                    socket = ReferenceEquals(_binding, binding) ? _socket : null;
                }

                if (wasConnected)
                {
                    await VerifyLiveLeaseAsync(binding, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception error) when (
                attempt == 0
                && error is
                    ToolTransportException
                    or WebSocketException
                    or IOException
                    or SocketException
                    or ObjectDisposedException)
            {
                if (socket is null
                    && ConnectionSetupFailureAsync is { } setupFailure)
                {
                    await setupFailure(cancellationToken).ConfigureAwait(false);
                }

                if (socket is not null)
                {
                    InvalidateConnection(binding, socket, connectionGeneration?.Value);
                }
            }
        }

        throw new ToolTransportException("broker_lease_handshake_failed");

    }

    private async Task VerifyLiveLeaseAsync(
        GatewayBinding binding,
        CancellationToken cancellationToken)
    {
        var expected = binding.RunOpenPayloadFactory();
        var generation = GetCurrentState(binding).Generation
            ?? throw new ToolTransportException("broker_lease_generation_changed");
        var opened = await RoundTripAsync(
                binding,
                AgentMessageTypes.RunOpen,
                expected,
                cancellationToken,
                expectedGeneration: generation)
            .ConfigureAwait(false);
        ValidateRunOpened(opened, expected);
    }

    private Task<AgentEnvelope> RpcAsync(
        string messageType,
        JsonObject payload,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null,
        GatewayGeneration? expectedGeneration = null,
        Action? onSendStarted = null) =>
        RpcAsync(
            messageType,
            _ => payload,
            static (reply, _) => reply,
            cancellationToken,
            expectedLease,
            expectedGeneration,
            onSendStarted);

    private Task<AgentEnvelope> RpcAsync(
        string messageType,
        Func<GatewayBinding, JsonObject> payloadFactory,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null,
        GatewayGeneration? expectedGeneration = null,
        Action? onSendStarted = null,
        Func<string, string>? replyTextTransform = null) =>
        RpcAsync(
            messageType,
            payloadFactory,
            static (reply, _) => reply,
            cancellationToken,
            expectedLease,
            expectedGeneration,
            onSendStarted,
            replyTextTransform);

    private async Task<T> RpcAsync<T>(
        string messageType,
        Func<GatewayBinding, JsonObject> payloadFactory,
        Func<AgentEnvelope, BrokerRunLeaseIdentity?, T> project,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null,
        GatewayGeneration? expectedGeneration = null,
        Action? onSendStarted = null,
        Func<string, string>? replyTextTransform = null)
    {
        using var activeRpc = EnterRpc();
        GatewayBinding? binding = null;
        ClientWebSocket? socket = null;
        GatewayGeneration? connectionGeneration = null;
        string? requestId = null;
        try
        {
            binding = GetBinding();
            await EnsureReadyAsync(binding, cancellationToken).ConfigureAwait(false);
            var state = GetCurrentState(binding);
            var lease = state.Lease;
            connectionGeneration = state.Generation;
            lock (_bindingSync)
            {
                socket = ReferenceEquals(_binding, binding) ? _socket : null;
            }

            requestId = $"gw-{Interlocked.Increment(ref _requestSequence)}";
            var completion = new TaskCompletionSource<AgentEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sendHook = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingReply = new PendingReply(completion, sendHook, replyTextTransform);
            lock (_bindingSync)
            {
                if (IsDisposed
                    || !ReferenceEquals(_binding, binding)
                    || _socket is null)
                {
                    throw new ToolTransportException("broker_connection_closed");
                }

                if (!_pendingReplies.TryAdd(requestId, pendingReply))
                {
                    throw new InvalidOperationException("duplicate_gateway_request_id");
                }
            }

            try
            {
                await SendAsync(
                        binding,
                        new AgentEnvelope(
                            AgentEnvelopeCodec.ProtocolVersionV2,
                            requestId,
                            messageType,
                            binding.SessionId,
                            null,
                            10_000,
                            payloadFactory(binding)),
                        cancellationToken,
                        expectedLease,
                        expectedGeneration,
                        onSendStarted)
                    .ConfigureAwait(false);
                if (messageType == AgentMessageTypes.ToolExecute
                    && AfterExecutionSendAsync is { } afterExecutionSend)
                {
                    await afterExecutionSend(cancellationToken).ConfigureAwait(false);
                }

                pendingReply.SendHook.TrySetResult(true);
                var reply = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return project(reply, lease);
            }
            finally
            {
                pendingReply.SendHook.TrySetResult(true);
                lock (_bindingSync)
                {
                    _pendingReplies.Remove(requestId);
                }
            }
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

    private void InvalidateConnection(
        GatewayBinding? binding = null,
        ClientWebSocket? expectedSocket = null,
        long? expectedConnectionGeneration = null)
    {
        _sendGate.Wait();
        try
        {
            BrokerRunLeaseIdentity? currentLease;
            ClientWebSocket? socket;
            CancellationTokenSource? receiveCancellation;
            PendingReply[] pendingReplies;
            lock (_bindingSync)
            {
                if (expectedConnectionGeneration is not null
                    && _connectionGeneration != expectedConnectionGeneration.Value)
                {
                    return;
                }

                if (binding is not null && !ReferenceEquals(_binding, binding))
                {
                    return;
                }

                if (expectedSocket is not null && !ReferenceEquals(_socket, expectedSocket))
                {
                    return;
                }

                currentLease = _currentLease;
                _currentLease = null;
                _currentGeneration = null;
                _pending.Clear();
                _cancelled.Clear();
                pendingReplies = _pendingReplies.Values.ToArray();
                _pendingReplies.Clear();
                receiveCancellation = _receiveCancellation;
                _receiveCancellation = null;
                _receiveLoop = null;
                socket = _socket;
                _socket = null;
                if (currentLease is not null)
                {
                    _approvals?.InvalidateLease(currentLease);
                }
            }

            receiveCancellation?.Cancel();
            CompletePending(pendingReplies, "broker_connection_closed");
            try
            {
                socket?.Dispose();
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private ActiveRpcScope EnterRpc()
    {
        lock (_rpcSync)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(BrokerToolGateway));
            }

            if (_activeRpcCount == 0)
            {
                _rpcsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _activeRpcCount++;
            return new ActiveRpcScope(this);
        }
    }

    private void ExitRpc()
    {
        lock (_rpcSync)
        {
            if (_activeRpcCount == 0)
            {
                return;
            }

            _activeRpcCount--;
            if (_activeRpcCount == 0)
            {
                _rpcsDrained.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> CompletedRpcSignal()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetResult(true);
        return completion;
    }

    private static void CompletePending(
        IEnumerable<PendingReply> pending,
        string errorCode)
    {
        var error = new ToolTransportException(errorCode);
        foreach (var item in pending)
        {
            item.Completion.TrySetException(error);
        }
    }

    private async Task EnsureReadyAsync(
        GatewayBinding binding,
        CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected(binding))
            {
                return;
            }

            var previousGeneration = Volatile.Read(ref _connectionGeneration);
            InvalidateConnection(
                binding,
                expectedConnectionGeneration: previousGeneration);
            var generation = Interlocked.Increment(ref _connectionGeneration);
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + binding.Bearer);
            try
            {
                await socket.ConnectAsync(binding.BrokerUrl, cancellationToken).ConfigureAwait(false);
                lock (_bindingSync)
                {
                    if (IsDisposed || !ReferenceEquals(_binding, binding))
                    {
                        throw new ToolTransportException("gateway_binding_changed");
                    }

                    _socket = socket;
                }

                var expected = binding.RunOpenPayloadFactory();
                var requestId = $"gw-{Interlocked.Increment(ref _requestSequence)}";
                await SendAsync(
                        binding,
                        new AgentEnvelope(
                            AgentEnvelopeCodec.ProtocolVersionV2,
                            requestId,
                            AgentMessageTypes.RunOpen,
                            binding.SessionId,
                            null,
                            10_000,
                            expected),
                        cancellationToken)
                    .ConfigureAwait(false);
                var opened = await ReceiveDirectAsync(socket, requestId, cancellationToken)
                    .ConfigureAwait(false);
                ValidateRunOpened(opened, expected);
                var lease = binding.Lease is null
                    ? null
                    : binding.Lease with { Generation = generation };
                var receiveCancellation = new CancellationTokenSource();
                Task receiveLoop;
                lock (_bindingSync)
                {
                    if (IsDisposed
                        || !ReferenceEquals(_binding, binding)
                        || !ReferenceEquals(_socket, socket))
                    {
                        receiveCancellation.Cancel();
                        throw new ToolTransportException("gateway_binding_changed");
                    }

                    _currentLease = lease;
                    _currentGeneration = new GatewayGeneration(lease, generation);
                    _receiveCancellation = receiveCancellation;
                    if (lease is not null)
                    {
                        _approvals?.ActivateLease(lease);
                    }
                }

                receiveLoop = ReceiveLoopAsync(
                    socket,
                    binding,
                    generation,
                    receiveCancellation.Token);
                lock (_bindingSync)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _receiveLoop = receiveLoop;
                    }
                    else
                    {
                        receiveCancellation.Cancel();
                    }
                }
            }
            catch
            {
                InvalidateConnection(binding, socket, generation);
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }

                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static void ValidateRunOpened(AgentEnvelope opened, JsonObject expected)
    {
        if (opened.MessageType != AgentMessageTypes.RunOpened
            || !string.Equals(
                opened.Payload["status"]?.GetValue<string>(),
                ToolResultStatuses.Succeeded,
                StringComparison.Ordinal)
            || !string.Equals(
                opened.Payload["run_id"]?.GetValue<string>(),
                expected["run_id"]?.GetValue<string>(),
                StringComparison.Ordinal)
            || opened.Payload["snapshot_version"]?.GetValue<long>() != expected["snapshot_version"]?.GetValue<long>())
        {
            throw new ToolTransportException("run_lease_mismatch");
        }
    }

    private GatewayBinding GetBinding()
    {
        lock (_bindingSync)
        {
            return _binding;
        }
    }

    private (BrokerRunLeaseIdentity? Lease, GatewayGeneration? Generation) GetCurrentState(
        GatewayBinding binding)
    {
        lock (_bindingSync)
        {
            return ReferenceEquals(_binding, binding)
                ? (_currentLease, _currentGeneration)
                : (null, null);
        }
    }

    private BrokerRunLeaseIdentity? GetCurrentLease(GatewayBinding binding)
    {
        lock (_bindingSync)
        {
            return ReferenceEquals(_binding, binding) ? _currentLease : null;
        }
    }

    private bool IsConnected(GatewayBinding binding)
    {
        lock (_bindingSync)
        {
            return ReferenceEquals(_binding, binding)
                && _socket is not null
                && _receiveLoop is not { IsCompleted: true };
        }
    }

    private Task<AgentEnvelope> RoundTripAsync(
        GatewayBinding binding,
        string messageType,
        JsonObject payload,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null,
        GatewayGeneration? expectedGeneration = null) =>
        RpcAsync(
            messageType,
            _ => payload,
            static (reply, _) => reply,
            cancellationToken,
            expectedLease,
            expectedGeneration);

    private async Task SendAsync(
        GatewayBinding binding,
        AgentEnvelope envelope,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null,
        GatewayGeneration? expectedGeneration = null,
        Action? onSendStarted = null)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientWebSocket socket;
            lock (_bindingSync)
            {
                if (!ReferenceEquals(_binding, binding)
                    || (expectedLease is not null && _currentLease != expectedLease)
                    || (expectedGeneration is not null && _currentGeneration != expectedGeneration))
                {
                    throw new ToolTransportException("broker_lease_generation_changed");
                }

                socket = _socket
                    ?? throw new ToolTransportException("broker_connection_closed");
            }

            onSendStarted?.Invoke();
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private const int MaxBrokerFrameBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static async Task<AgentEnvelope> ReceiveDirectAsync(
        ClientWebSocket socket,
        string requestId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var text = await ReceiveTextFrameAsync(socket, buffer, cancellationToken)
                .ConfigureAwait(false);
            var reply = AgentEnvelopeCodec.Parse(text);
            if (reply.RequestId == requestId)
            {
                return reply;
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        GatewayBinding binding,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var text = await ReceiveTextFrameAsync(socket, buffer, cancellationToken)
                    .ConfigureAwait(false);
                var routed = RouteReply(text);
                if (routed is null)
                {
                    continue;
                }

                PendingReply pending;
                lock (_bindingSync)
                {
                    if (!_pendingReplies.TryGetValue(routed.RequestId, out pending!))
                    {
                        continue;
                    }
                }

                try
                {
                    await pending.SendHook.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    continue;
                }

                lock (_bindingSync)
                {
                    if (_pendingReplies.TryGetValue(routed.RequestId, out var current)
                        && ReferenceEquals(current, pending))
                    {
                        _pendingReplies.Remove(routed.RequestId);
                        pending.Completion.TrySetResult(routed);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            try
            {
                InvalidateConnection(binding, socket, connectionGeneration);
            }
            catch (Exception)
            {
            }
        }
    }

    private AgentEnvelope? RouteReply(string text)
    {
        AgentEnvelope reply;
        try
        {
            reply = AgentEnvelopeCodec.Parse(text);
        }
        catch (AgentProtocolException original)
        {
            KeyValuePair<string, PendingReply>[] candidates;
            lock (_bindingSync)
            {
                candidates = _pendingReplies
                    .Where(item => item.Value.ReplyTextTransform is not null)
                    .ToArray();
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var candidateReply = AgentEnvelopeCodec.Parse(
                        candidate.Value.ReplyTextTransform!(text));
                    if (string.Equals(
                            candidateReply.RequestId,
                            candidate.Key,
                            StringComparison.Ordinal))
                    {
                        return candidateReply;
                    }
                }
                catch (AgentProtocolException)
                {
                }
            }

            ExceptionDispatchInfo.Capture(original).Throw();
            return null;
        }

        Func<string, string>? transform;
        lock (_bindingSync)
        {
            if (!_pendingReplies.TryGetValue(reply.RequestId, out var pending))
            {
                return null;
            }

            transform = pending.ReplyTextTransform;
        }

        if (transform is null)
        {
            return reply;
        }

        var transformedReply = AgentEnvelopeCodec.Parse(transform(text));
        if (!string.Equals(transformedReply.RequestId, reply.RequestId, StringComparison.Ordinal))
        {
            throw new ToolTransportException("broker_reply_identity_mismatch");
        }

        return transformedReply;
    }

    private static async Task<string> ReceiveTextFrameAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken)
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

            try
            {
                return StrictUtf8.GetString(content.GetBuffer(), 0, (int)content.Length);
            }
            catch (DecoderFallbackException)
            {
                throw new ToolTransportException("broker_invalid_utf8");
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

    private static bool IsPostDispatchExecuteFailure(
        ExecutionDispatchState dispatch,
        ToolAccessKind accessKind,
        Exception error) =>
        dispatch.IsDispatched
        && accessKind != ToolAccessKind.ReadOnly
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
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.OutcomeUnknown,
            "transport_failure",
            null,
            sideEffectsPossible: true);
    }

    internal static ToolAccessKind AccessKindForTool(string toolId, JsonObject? arguments = null) =>
        toolId switch
        {
            "write" or "edit" or "todo_write" => ToolAccessKind.Mutation,
            "bash" => ToolAccessKind.Terminal,
            _ when BrokerPolicyContracts.IsReadOnlyTool(toolId) => ToolAccessKind.ReadOnly,
            _ => ToolAccessKind.Terminal,
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
