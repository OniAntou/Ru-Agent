using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Worker;

namespace RuBackend.Runtime;

public static class VoiceStreamEndpoint
{
    private const int MaxMessageBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Map(WebApplication app)
    {
        app.MapGet("/voice/stream", HandleAsync);
    }

    private static async Task HandleAsync(
        HttpContext context,
        IVoiceProtocolClient protocol,
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
        using var sendLock = new SemaphoreSlim(1, 1);
        Task? activeStream = null;
        CancellationTokenSource? activeCancellation = null;
        string? activeRequestId = null;
        try
        {
            while (!connectionCancellation.IsCancellationRequested
                   && socket.State == WebSocketState.Open)
            {
                if (activeStream is null)
                {
                    var initialInbound = await ReceiveAsync(socket, connectionCancellation.Token);
                    if (initialInbound.Closed)
                    {
                        return;
                    }
                    if (!TryParseControl(initialInbound, out var initialControl))
                    {
                        await SendFailureAsync(socket, sendLock, string.Empty, "invalid_request", connectionCancellation.Token);
                        continue;
                    }
                    if (initialControl!.Type != "start")
                    {
                        if (initialControl.Type is "cancel" or "playback")
                        {
                            continue;
                        }
                        await SendFailureAsync(socket, sendLock, initialControl.RequestId, "invalid_request", connectionCancellation.Token);
                        continue;
                    }
                    if (!TryBuildRequest(initialControl, out var request))
                    {
                        await SendFailureAsync(socket, sendLock, initialControl.RequestId, "invalid_request", connectionCancellation.Token);
                        continue;
                    }

                    activeRequestId = request!.RequestId;
                    activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        connectionCancellation.Token);
                    activeStream = StreamAsync(
                        socket,
                        sendLock,
                        protocol,
                        request,
                        activeCancellation.Token);
                    continue;
                }

                using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    connectionCancellation.Token);
                var receiveTask = ReceiveAsync(socket, receiveCancellation.Token);
                var completed = await Task.WhenAny(activeStream, receiveTask);
                if (completed == activeStream)
                {
                    receiveCancellation.Cancel();
                    await IgnoreReceiveCancellationAsync(receiveTask);
                    await ObserveStreamAsync(activeStream, socket, sendLock, activeRequestId!, connectionCancellation.Token);
                    activeCancellation?.Dispose();
                    activeCancellation = null;
                    activeStream = null;
                    activeRequestId = null;
                    continue;
                }

                var inbound = await receiveTask;
                if (inbound.Closed)
                {
                    return;
                }
                if (!TryParseControl(inbound, out var control))
                {
                    await SendFailureAsync(socket, sendLock, activeRequestId!, "invalid_request", connectionCancellation.Token);
                    continue;
                }
                if (control!.Type == "cancel" && control.RequestId == activeRequestId)
                {
                    await SendSignalBestEffortAsync(
                        protocol,
                        new WorkerVoiceSignal(activeRequestId!, "cancel"),
                        connectionCancellation.Token);
                }
                else if (control.Type == "playback" && control.RequestId == activeRequestId
                         && control.State is "started" or "ended" or "cancelled" or "failed")
                {
                    await SendSignalBestEffortAsync(
                        protocol,
                        new WorkerVoiceSignal(activeRequestId!, "playback", control.State),
                        connectionCancellation.Token);
                }
                else
                {
                    await SendFailureAsync(socket, sendLock, activeRequestId!, "stream_active", connectionCancellation.Token);
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
            if (activeRequestId is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendSignalBestEffortAsync(
                    protocol,
                    new WorkerVoiceSignal(activeRequestId, "cancel"),
                    timeout.Token);
            }
            activeCancellation?.Cancel();
            if (activeStream is not null)
            {
                try
                {
                    await activeStream;
                }
                catch (Exception error) when (error is OperationCanceledException
                    or WorkerProtocolException
                    or WorkerUnavailableException)
                {
                }
            }
            activeCancellation?.Dispose();
        }
    }

    private static async Task StreamAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        IVoiceProtocolClient protocol,
        WorkerVoiceStreamRequest request,
        CancellationToken cancellationToken)
    {
        var terminal = false;
        await foreach (var item in protocol.StreamVoiceAsync(request, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (item.Type == WorkerMessageTypes.Error)
            {
                var code = ReadString(item.Payload["code"]) ?? "worker_error";
                await SendFailureAsync(socket, sendLock, request.RequestId, code, cancellationToken);
                terminal = true;
                break;
            }
            if (item.Type is not (WorkerMessageTypes.VoiceControl or WorkerMessageTypes.VoiceDone))
            {
                throw new WorkerProtocolException("unexpected_type");
            }

            var publicPayload = item.Payload.DeepClone().AsObject();
            publicPayload.Remove("state");
            publicPayload.Remove("byte_length");
            var type = ReadString(publicPayload["type"]);
            var requestId = ReadString(publicPayload["request_id"]);
            if (string.IsNullOrWhiteSpace(type)
                || !string.Equals(requestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException("invalid_voice_event");
            }

            if (type == "audio")
            {
                if (item.Binary.IsEmpty)
                {
                    throw new WorkerProtocolException("missing_binary");
                }
                await SendJsonAndBinaryAsync(
                    socket,
                    sendLock,
                    publicPayload,
                    item.Binary,
                    cancellationToken);
                continue;
            }
            if (!item.Binary.IsEmpty)
            {
                throw new WorkerProtocolException("unexpected_binary");
            }
            await SendJsonAsync(socket, sendLock, publicPayload, cancellationToken);
            if (item.Type == WorkerMessageTypes.VoiceDone)
            {
                terminal = true;
                break;
            }
        }
        if (!terminal)
        {
            throw new WorkerProtocolException("missing_terminal");
        }
    }

    private static async Task ObserveStreamAsync(
        Task stream,
        WebSocket socket,
        SemaphoreSlim sendLock,
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WorkerProtocolException or WorkerUnavailableException)
        {
            await SendFailureAsync(socket, sendLock, requestId, "worker_protocol_error", cancellationToken);
        }
    }

    private static bool TryBuildRequest(VoiceControl control, out WorkerVoiceStreamRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(control.RequestId) || control.RequestId.Length > 128
            || string.IsNullOrWhiteSpace(control.Text) || control.Text.Length > 12_000
            || control.VoiceLabel?.Length > 128
            || (control.Speed is not null
                && (control.Speed is < 0.5 or > 2.0
                    || !double.IsFinite(control.Speed.Value))))
        {
            return false;
        }
        request = new WorkerVoiceStreamRequest(
            control.RequestId,
            control.Text,
            control.VoiceLabel,
            control.Speed,
            "original");
        return true;
    }

    private static bool TryParseControl(Inbound inbound, out VoiceControl? control)
    {
        control = null;
        if (inbound.MessageType != WebSocketMessageType.Text || inbound.TooLarge)
        {
            return false;
        }
        try
        {
            var text = StrictUtf8.GetString(inbound.Bytes);
            if (JsonNode.Parse(text) is not JsonObject root
                || ReadString(root["type"]) is not { } type)
            {
                return false;
            }
            control = new VoiceControl(
                type,
                ReadString(root["request_id"]) ?? string.Empty,
                ReadString(root["text"]),
                ReadString(root["voice_label"]),
                ReadDouble(root["speed"]),
                ReadString(root["state"]));
            return true;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static async Task<Inbound> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketMessageType? messageType = null;
        var tooLarge = false;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new Inbound(result.MessageType, [], false, true);
            }
            messageType ??= result.MessageType;
            if (messageType != result.MessageType)
            {
                return new Inbound(result.MessageType, [], false, false);
            }
            if (!tooLarge && stream.Length + result.Count > MaxMessageBytes)
            {
                tooLarge = true;
            }
            if (!tooLarge)
            {
                stream.Write(buffer, 0, result.Count);
            }
            if (result.EndOfMessage)
            {
                return new Inbound(messageType.Value, tooLarge ? [] : stream.ToArray(), tooLarge, false);
            }
        }
    }

    private static Task SendFailureAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        string requestId,
        string code,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            socket,
            sendLock,
            new JsonObject
            {
                ["type"] = "failed",
                ["request_id"] = requestId,
                ["sequence"] = 0,
                ["error_code"] = code,
            },
            cancellationToken);

    private static async Task SendJsonAndBinaryAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        JsonObject payload,
        ReadOnlyMemory<byte> binary,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(payload.ToJsonString()),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
            await socket.SendAsync(binary, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task SendJsonAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(payload.ToJsonString()),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task SendSignalBestEffortAsync(
        IVoiceProtocolClient protocol,
        WorkerVoiceSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            await protocol.SendVoiceSignalAsync(signal, cancellationToken);
        }
        catch (Exception error) when (error is WorkerProtocolException
            or WorkerUnavailableException
            or OperationCanceledException)
        {
        }
    }

    private static async Task IgnoreReceiveCancellationAsync(Task<Inbound> receive)
    {
        try
        {
            await receive;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var result))
        {
            return result;
        }
        return value.TryGetValue<int>(out var integer) ? integer : null;
    }

    private sealed record VoiceControl(
        string Type,
        string RequestId,
        string? Text,
        string? VoiceLabel,
        double? Speed,
        string? State);

    private sealed record Inbound(
        WebSocketMessageType MessageType,
        byte[] Bytes,
        bool TooLarge,
        bool Closed);
}
