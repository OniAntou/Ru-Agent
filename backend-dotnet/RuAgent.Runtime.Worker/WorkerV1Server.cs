using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Runtime.Worker;

public sealed class WorkerV1Server
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> RequestTypes = new HashSet<string>(
        [
            WorkerV1Contracts.RuntimeLoad,
            WorkerV1Contracts.RuntimeRequest,
            WorkerV1Contracts.VoiceStart,
            WorkerV1Contracts.VtsCatalog,
            WorkerV1Contracts.VtsAction,
        ],
        StringComparer.Ordinal);
    private readonly WorkerV1Options _options;
    private readonly IWorkerV1RequestRouter _router;

    public WorkerV1Server(string token, IWorkerV1RequestRouter? router = null)
        : this(new WorkerV1Options(token), router ?? new UnavailableWorkerV1RequestRouter())
    {
    }

    public WorkerV1Server(WorkerV1Options options, IWorkerV1RequestRouter router)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Path, "/worker/v1", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await HandleConnectionAsync(socket, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(WebSocket socket, CancellationToken connectionCancellation)
    {
        using var sendLock = new SemaphoreSlim(1, 1);
        var requestTasks = new Dictionary<string, Task>(StringComparer.Ordinal);
        var cancellations = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);

        long NextSequence(string id)
        {
            var next = sequences.TryGetValue(id, out var value) ? value : 0;
            sequences[id] = checked(next + 1);
            return next;
        }

        try
        {
            string? helloFrame;
            try
            {
                helloFrame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
            }
            catch (AgentProtocolException error)
            {
                await SendErrorAsync(socket, sendLock, "connection", error.Code, false, 0, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (helloFrame is null)
            {
                return;
            }

            WorkerV1Envelope hello;
            try
            {
                hello = WorkerV1EnvelopeCodec.Parse(helloFrame);
            }
            catch (AgentProtocolException error)
            {
                await SendErrorAsync(socket, sendLock, "connection", error.Code, false, 0, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (hello.Type != WorkerV1Contracts.Hello)
            {
                await SendErrorAsync(socket, sendLock, hello.Id, "hello_required", false, 0, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (!IsAuthorized(hello.Payload["token"]))
            {
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            var capabilities = new JsonArray("runtime", "voice", "vts", "observation");
            await SendAsync(
                socket,
                sendLock,
                new WorkerV1Envelope(
                    WorkerV1Contracts.ProtocolVersion,
                    hello.Id,
                    WorkerV1Contracts.HelloOk,
                    0,
                    hello.CorrelationId,
                    new JsonObject
                    {
                        ["version"] = WorkerV1Contracts.ProtocolVersion,
                        ["capabilities"] = capabilities,
                    }),
                connectionCancellation).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open && !connectionCancellation.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
                }
                catch (AgentProtocolException error)
                {
                    await SendErrorAsync(socket, sendLock, "connection", error.Code, false, NextSequence("connection"), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (frame is null)
                {
                    break;
                }

                WorkerV1Envelope envelope;
                try
                {
                    envelope = WorkerV1EnvelopeCodec.Parse(frame);
                }
                catch (AgentProtocolException error)
                {
                    await SendErrorAsync(socket, sendLock, SafeRequestId(frame), error.Code, false, NextSequence(SafeRequestId(frame)), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == WorkerV1Contracts.WorkerHealth)
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        new WorkerV1Envelope(
                            WorkerV1Contracts.ProtocolVersion,
                            envelope.Id,
                            WorkerV1Contracts.WorkerHealthOk,
                            0,
                            envelope.CorrelationId,
                            new JsonObject
                            {
                                ["ready"] = true,
                                ["version"] = WorkerV1Contracts.ProtocolVersion,
                                ["capabilities"] = capabilities.DeepClone(),
                            }),
                        connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (!RequestTypes.Contains(envelope.Type))
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "unsupported_operation", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (requestTasks.TryGetValue(envelope.Id, out var existing) && !existing.IsCompleted)
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "duplicate_request", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
                cancellations[envelope.Id] = requestCancellation;
                var task = RunRequestAsync(
                    socket,
                    sendLock,
                    envelope,
                    requestCancellation,
                    NextSequence,
                    requestTasks,
                    cancellations);
                requestTasks[envelope.Id] = task;
            }
        }
        catch (OperationCanceledException)
        {
            // Connection shutdown is a normal lifecycle path.
        }
        catch (WebSocketException)
        {
            // The peer went away; no error payload can be delivered safely.
        }
        finally
        {
            foreach (var cancellation in cancellations.Values)
            {
                cancellation.Cancel();
            }

            if (requestTasks.Count > 0)
            {
                await Task.WhenAll(requestTasks.Values).ConfigureAwait(false);
            }

            foreach (var cancellation in cancellations.Values)
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task RunRequestAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WorkerV1Envelope request,
        CancellationTokenSource cancellationSource,
        Func<string, long> nextSequence,
        IDictionary<string, Task> requestTasks,
        IDictionary<string, CancellationTokenSource> cancellations)
    {
        try
        {
            await foreach (var reply in _router.RouteAsync(
                request.Type,
                (JsonObject)request.Payload.DeepClone(),
                cancellationSource.Token).WithCancellation(cancellationSource.Token).ConfigureAwait(false))
            {
                cancellationSource.Token.ThrowIfCancellationRequested();
                var payload = (JsonObject)reply.Payload.DeepClone();
                var announcesBinary = AnnouncesBinary(reply.Type, payload);
                if (announcesBinary != !reply.Binary.IsEmpty)
                {
                    throw new AgentProtocolException("protocol_failure");
                }

                if (!reply.Binary.IsEmpty)
                {
                    payload["byte_length"] = reply.Binary.Length;
                }

                await SendAsync(
                    socket,
                    sendLock,
                    new WorkerV1Envelope(
                        WorkerV1Contracts.ProtocolVersion,
                        request.Id,
                        reply.Type,
                        nextSequence(request.Id),
                        request.CorrelationId,
                        payload),
                    cancellationSource.Token,
                    reply.Binary).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await SendErrorAsync(socket, sendLock, request.Id, "cancelled", false, nextSequence(request.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentProtocolException error)
        {
            await SendErrorAsync(socket, sendLock, request.Id, error.Code, false, nextSequence(request.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendErrorAsync(socket, sendLock, request.Id, "worker_failure", true, nextSequence(request.Id), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            requestTasks.Remove(request.Id);
            cancellations.Remove(request.Id);
            cancellationSource.Dispose();
        }
    }

    private bool IsAuthorized(JsonNode? tokenNode)
    {
        if (tokenNode is not JsonValue value
            || !value.TryGetValue<string>(out var supplied)
            || supplied.Length != _options.Token.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            StrictUtf8.GetBytes(supplied),
            StrictUtf8.GetBytes(_options.Token));
    }

    private static bool AnnouncesBinary(string type, JsonObject payload) =>
        type == WorkerV1Contracts.VoiceControl
            && payload["state"] is JsonValue state
            && state.TryGetValue<string>(out var stateText)
            && string.Equals(stateText, "audio_ready", StringComparison.Ordinal)
        || type == WorkerV1Contracts.RuntimeResponse
            && payload["binary"] is JsonValue binary
            && binary.TryGetValue<bool>(out var binaryValue)
            && binaryValue;

    private static async Task SendErrorAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        string id,
        string code,
        bool retryable,
        long sequence,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            socket,
            sendLock,
            new WorkerV1Envelope(
                WorkerV1Contracts.ProtocolVersion,
                id,
                WorkerV1Contracts.Error,
                sequence,
                null,
                new JsonObject
                {
                    ["code"] = code,
                    ["retryable"] = retryable,
                }),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WorkerV1Envelope envelope,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> binary = default)
    {
        var bytes = StrictUtf8.GetBytes(WorkerV1EnvelopeCodec.Serialize(envelope));
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            if (!binary.IsEmpty)
            {
                await socket.SendAsync(binary, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new AgentProtocolException("text_control_required");
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > WorkerV1Contracts.MaxFrameBytes)
            {
                throw new AgentProtocolException("message_too_large");
            }

            if (result.EndOfMessage)
            {
                try
                {
                    return StrictUtf8.GetString(stream.ToArray());
                }
                catch (DecoderFallbackException error)
                {
                    throw new AgentProtocolException("invalid_utf8", error.Message);
                }
            }
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(status, status.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SafeRequestId(string frame)
    {
        try
        {
            var value = JsonNode.Parse(frame) as JsonObject;
            var id = value?["id"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(id) && id.Length <= WorkerV1Contracts.MaxRequestIdChars
                ? id
                : "connection";
        }
        catch
        {
            return "connection";
        }
    }
}
