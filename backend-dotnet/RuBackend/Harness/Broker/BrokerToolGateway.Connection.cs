using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed partial class BrokerToolGateway
{
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
}
