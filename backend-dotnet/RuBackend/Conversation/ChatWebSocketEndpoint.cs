using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Conversation;

public static class ChatWebSocketEndpoint
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var ownership = app.Services
            .GetRequiredService<IOptions<BackendOptions>>()
            .Value
            .Ownership
            .Chat;
        if (!string.Equals(ownership?.Trim(), "aspnet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        app.MapGet("/chat", HandleAsync);
        return true;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IConversationCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.RequestAborted);
        try
        {
            while (!connectionCancellation.IsCancellationRequested
                   && socket.State == WebSocketState.Open)
            {
                var inbound = await ReceiveMessageAsync(
                    socket,
                    connectionCancellation.Token).ConfigureAwait(false);
                if (inbound.Closed)
                {
                    return;
                }

                if (inbound.TooLarge)
                {
                    await SendAsync(
                        socket,
                        Error("message_too_large", "Message too large."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                ChatRequest? request = null;
                ChatEvent? parseError = null;
                if (inbound.MessageType != WebSocketMessageType.Text
                    || !TryParseRequest(inbound.Bytes, out request, out parseError))
                {
                    await SendAsync(
                        socket,
                        parseError ?? Error("malformed_message", "Malformed message."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    continue;
                }

                var prompt = request.Prompt.Trim();
                if (prompt.Length > ChatRequest.MaxPromptCharacters)
                {
                    await SendAsync(
                        socket,
                        Error(
                            "prompt_too_long",
                            $"Prompt too long (max {ChatRequest.MaxPromptCharacters} characters)."),
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                var closed = await StreamTurnAsync(
                    socket,
                    coordinator,
                    new ChatRequest(prompt, request.SessionId?.Trim()),
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
            connectionCancellation.Cancel();
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

    private static async Task<bool> StreamTurnAsync(
        WebSocket socket,
        IConversationCoordinator coordinator,
        ChatRequest request,
        CancellationToken connectionCancellation)
    {
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connectionCancellation);
        var monitor = MonitorDisconnectAsync(socket, turnCancellation);
        var closed = false;
        try
        {
            await foreach (var item in coordinator.HandleAsync(request, turnCancellation.Token)
                .WithCancellation(turnCancellation.Token).ConfigureAwait(false))
            {
                await SendAsync(socket, item, turnCancellation.Token).ConfigureAwait(false);
                if (item.Type is "done" or "error")
                {
                    break;
                }
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
        CancellationTokenSource turnCancellation)
    {
        var buffer = new byte[4 * 1024];
        while (!turnCancellation.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(
                buffer,
                turnCancellation.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                turnCancellation.Cancel();
                return true;
            }

            while (!result.EndOfMessage)
            {
                result = await socket.ReceiveAsync(
                    buffer,
                    turnCancellation.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    turnCancellation.Cancel();
                    return true;
                }
            }
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
        out ChatEvent? error)
    {
        request = null;
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

            request = new ChatRequest(prompt, sessionId);
            return true;
        }
        catch (Exception parseError) when (parseError is JsonException or DecoderFallbackException)
        {
            error = Error("malformed_message", "Malformed message.");
            return false;
        }
    }

    private static async Task SendAsync(
        WebSocket socket,
        ChatEvent value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    private static ChatEvent Error(string code, string content) =>
        new("error", content, ErrorCode: code);

    private sealed record InboundMessage(
        WebSocketMessageType MessageType,
        byte[] Bytes,
        bool TooLarge,
        bool Closed);
}
