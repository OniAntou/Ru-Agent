using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using RuBackend.Agent;
using RuBackend.Diagnostics;
namespace RuBackend.Conversation;

public static class ChatWebSocketEndpoint
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/chat", HandleAsync);
    }

    private static async Task HandleAsync(
        HttpContext context,
        IConversationCoordinator coordinator,
        IAgentApprovalCoordinator approvalCoordinator,
        IActionOutcomeContextStore actionOutcomes,
        IDiagnosticEmitter? diagnostics,
        RuAgent.Hosting.RuntimeClientLeaseRegistry leaseRegistry,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // Acquire client lease for lifetime of accepted WebSocket (per spec)
        using var lease = leaseRegistry.Acquire("chat");
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.RequestAborted);
        using var sendLock = new SemaphoreSlim(1, 1);
        var inbound = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        Task receiveLoop = ReceiveLoopAsync(
            socket,
            inbound.Writer,
            connectionCancellation.Token);
        string? activeSessionId = null;

        void ObserveSession(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (activeSessionId is not null
                && !string.Equals(activeSessionId, candidate, StringComparison.Ordinal))
            {
                actionOutcomes.Remove(activeSessionId);
            }

            activeSessionId = candidate;
        }

        try
        {
            await foreach (var message in inbound.Reader
                .ReadAllAsync(connectionCancellation.Token)
                .ConfigureAwait(false))
            {
                if (message.Closed)
                {
                    return;
                }

                if (message.TooLarge)
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        Error("message_too_large", "Message too large."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (message.MessageType == WebSocketMessageType.Text
                    && TryParseApprovalResponse(message.Bytes, out var approvalResponse, out var approvalError))
                {
                    if (approvalError is not null)
                    {
                        await SendAsync(
                            socket,
                            sendLock,
                            approvalError,
                            connectionCancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    var approvalResult = await approvalCoordinator.RespondAsync(
                        approvalResponse!,
                        connectionCancellation.Token).ConfigureAwait(false);
                    await SendAsync(
                        socket,
                        sendLock,
                        new ChatEvent(
                            "agent.approval_result",
                            approvalResult.SafeSummary,
                            approvalResponse!.SessionId,
                            approvalResult.ErrorCode,
                            approvalResult.Status,
                            approvalResponse.ApprovalId),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                ChatRequest? request = null;
                ChatEvent? parseError = null;
                var modeClamped = false;
                if (message.MessageType != WebSocketMessageType.Text
                    || !TryParseRequest(message.Bytes, out request, out modeClamped, out parseError))
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        parseError ?? Error("malformed_message", "Malformed message."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    continue;
                }

                if (modeClamped)
                {
                    EmitModeClamped(diagnostics, request.CorrelationId);
                }

                var prompt = request.Prompt.Trim();
                if (prompt.Length > ChatRequest.MaxPromptCharacters)
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        Error(
                            "prompt_too_long",
                            $"Prompt too long (max {ChatRequest.MaxPromptCharacters} characters)."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                ObserveSession(request.SessionId);
                var closed = await StreamTurnAsync(
                    socket,
                    sendLock,
                    coordinator,
                    new ChatRequest(
                        prompt,
                        request.SessionId?.Trim(),
                        request.CorrelationId,
                        request.Profile),
                    inbound.Reader,
                    ObserveSession,
                    connectionCancellation.Token).ConfigureAwait(false);
                if (closed)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WebSocketException or IOException)
        {
        }
        finally
        {
            if (activeSessionId is not null)
            {
                actionOutcomes.Remove(activeSessionId);
            }

            connectionCancellation.Cancel();
            inbound.Writer.TryComplete();
            try
            {
                await receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closed",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        ChannelWriter<InboundMessage> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                InboundMessage message = await ReceiveMessageAsync(
                    socket,
                    cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                if (message.Closed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task<bool> StreamTurnAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        IConversationCoordinator coordinator,
        ChatRequest request,
        ChannelReader<InboundMessage> inbound,
        Action<string?> observeSession,
        CancellationToken connectionCancellation)
    {
        using var correlationScope = CorrelationContext.Push(request.CorrelationId);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connectionCancellation);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connectionCancellation);
        var monitor = MonitorDisconnectAsync(
            socket,
            inbound,
            sendLock,
            turnCancellation,
            monitorCancellation.Token);
        var closed = false;
        try
        {
            await foreach (var item in coordinator.HandleAsync(request, turnCancellation.Token)
                .WithCancellation(turnCancellation.Token).ConfigureAwait(false))
            {
                observeSession(item.SessionId);
                if (item.Type is "done" or "error")
                {
                    // Stop the disconnect reader before publishing the terminal
                    // event. Otherwise it can consume the next request after
                    // the client observes this event, racing the main reader.
                    monitorCancellation.Cancel();
                    await SendAsync(socket, sendLock, item, connectionCancellation)
                        .ConfigureAwait(false);
                    break;
                }

                await SendAsync(socket, sendLock, item, turnCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WebSocketException or IOException)
        {
            closed = true;
            turnCancellation.Cancel();
        }
        finally
        {
            turnCancellation.Cancel();
            monitorCancellation.Cancel();
            try
            {
                closed |= await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
                closed = true;
            }
        }

        return closed || socket.State is WebSocketState.Closed or WebSocketState.Aborted;
    }

    private static async Task<bool> MonitorDisconnectAsync(
        WebSocket socket,
        ChannelReader<InboundMessage> inbound,
        SemaphoreSlim sendLock,
        CancellationTokenSource turnCancellation,
        CancellationToken monitorCancellation)
    {
        try
        {
            await foreach (InboundMessage message in inbound
                .ReadAllAsync(monitorCancellation)
                .ConfigureAwait(false))
            {
                if (message.Closed)
                {
                    turnCancellation.Cancel();
                    return true;
                }

                try
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        Error("busy", "Turn is already in progress."),
                        monitorCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
                {
                    return false;
                }
                catch (WebSocketException)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
        }

        return false;
    }

    private static async Task<InboundMessage> ReceiveMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketMessageType? messageType = null;
        var tooLarge = false;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new InboundMessage(result.MessageType, [], false, true);
            }

            messageType ??= result.MessageType;
            if (messageType != result.MessageType)
            {
                return new InboundMessage(result.MessageType, [], false, false);
            }

            if (!tooLarge && stream.Length + result.Count > ChatRequest.MaxMessageBytes)
            {
                tooLarge = true;
            }

            if (!tooLarge)
            {
                stream.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                return new InboundMessage(
                    messageType.Value,
                    tooLarge ? [] : stream.ToArray(),
                    tooLarge,
                    false);
            }
        }
    }

    private static bool TryParseRequest(
        byte[] bytes,
        out ChatRequest? request,
        out bool modeClamped,
        out ChatEvent? error)
    {
        request = null;
        modeClamped = false;
        error = null;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            if (JsonNode.Parse(text) is not JsonObject root
                || root["prompt"] is not JsonValue promptValue
                || !promptValue.TryGetValue<string>(out var prompt))
            {
                error = Error("malformed_message", "Malformed message.");
                return false;
            }

            string? sessionId = null;
            if (root["session_id"] is JsonNode sessionNode)
            {
                if (sessionNode is not JsonValue sessionValue
                    || !sessionValue.TryGetValue<string>(out sessionId))
                {
                    error = Error("malformed_message", "Malformed message.");
                    return false;
                }
            }

            string? correlationId = null;
            if (root["correlation_id"] is JsonValue corrValue && corrValue.TryGetValue<string>(out var parsedCorr))
            {
                correlationId = parsedCorr;
            }

            string? mode = null;
            if (root["mode"] is JsonNode modeNode)
            {
                if (modeNode is not JsonValue modeValue
                    || !modeValue.TryGetValue<string>(out mode))
                {
                    error = Error("malformed_message", "Malformed message.");
                    return false;
                }
            }

            var legacyAgentMode = false;
            if (root["agent_mode"] is JsonNode agentNode)
            {
                if (agentNode is not JsonValue agentValue
                    || !agentValue.TryGetValue<bool>(out legacyAgentMode))
                {
                    error = Error("malformed_message", "Malformed message.");
                    return false;
                }
            }

            (var profile, modeClamped) =
                AgentTurnCoordinator.ProfileForTurn(mode, legacyAgentMode);
            request = new ChatRequest(prompt, sessionId, correlationId, profile);
            return true;
        }
        catch (Exception parseError) when (parseError is JsonException or DecoderFallbackException)
        {
            error = Error("malformed_message", "Malformed message.");
            return false;
        }
    }

    private static bool TryParseApprovalResponse(
        byte[] bytes,
        out AgentApprovalResponse? response,
        out ChatEvent? error)
    {
        response = null;
        error = null;
        try
        {
            if (JsonNode.Parse(StrictUtf8.GetString(bytes)) is not JsonObject root
                || root["type"] is not JsonValue typeValue
                || !typeValue.TryGetValue<string>(out var type)
                || !string.Equals(type, "agent.approval.response", StringComparison.Ordinal))
            {
                return false;
            }

            var allowed = new HashSet<string>(
                ["type", "session_id", "approval_id", "approved"],
                StringComparer.Ordinal);
            var sessionId = string.Empty;
            var approvalId = string.Empty;
            if (root.Any(pair => !allowed.Contains(pair.Key))
                || !ReadBoundedString(root, "session_id", 128, out sessionId)
                || !ReadBoundedString(root, "approval_id", 128, out approvalId)
                || root["approved"] is not JsonValue approvedValue
                || !approvedValue.TryGetValue<bool>(out var approved))
            {
                error = new ChatEvent(
                    "agent.approval_result",
                    "Approval request is invalid.",
                    sessionId,
                    "invalid_approval");
                return true;
            }

            response = new AgentApprovalResponse(approvalId, sessionId, approved);
            return true;
        }
        catch (Exception parseError) when (parseError is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool ReadBoundedString(
        JsonObject root,
        string key,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        if (root[key] is not JsonValue node
            || !node.TryGetValue<string>(out var parsed)
            || string.IsNullOrWhiteSpace(parsed)
            || parsed.Length > maxLength)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static async Task SendAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        ChatEvent value,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static ChatEvent Error(string code, string content) =>
        new("error", content, ErrorCode: code);

    private static void EmitModeClamped(IDiagnosticEmitter? diagnostics, string? correlationId)
    {
        try
        {
            diagnostics?.TryEmit(
                "warning",
                "host.conversation",
                DiagnosticCodes.ModeClamped,
                correlationId);
        }
        catch
        {
            // Diagnostics must never break the chat loop.
        }
    }

    private sealed record InboundMessage(
        WebSocketMessageType MessageType,
        byte[] Bytes,
        bool TooLarge,
        bool Closed);
}
