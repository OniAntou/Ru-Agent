using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RuBackend.Compatibility;
using RuBackend.Configuration;

namespace RuBackend.Worker;

public sealed class FastApiWorkerClient : IWorkerClient
{
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(
        ["chat", "voice", "vts", "observation", "fastapi.rollback"],
        StringComparer.Ordinal);

    private readonly IUpstreamHttpTransport _httpTransport;
    private readonly IUpstreamWebSocketConnector _webSocketConnector;
    private readonly IOptions<BackendOptions> _options;
    private readonly ConcurrentDictionary<string, WebSocket> _activeChats =
        new(StringComparer.Ordinal);
    private int _disposed;

    public FastApiWorkerClient(
        IUpstreamHttpTransport httpTransport,
        IUpstreamWebSocketConnector webSocketConnector,
        IOptions<BackendOptions> options)
    {
        _httpTransport = httpTransport;
        _webSocketConnector = webSocketConnector;
        _options = options;
    }

    public async Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken)
    {
        var health = await GetHealthAsync(cancellationToken);
        return new WorkerHello(health.Version, health.Capabilities);
    }

    public async IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
        WorkerChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        WorkerEnvelopeCodec.Validate(new WorkerEnvelope(
            WorkerClientOptions.CurrentProtocolVersion,
            request.RequestId,
            WorkerMessageTypes.ChatStart,
            null,
            []));

        var token = _options.Value.DesktopToken?.Trim() ?? string.Empty;
        var uri = new Uri(
            $"ws://127.0.0.1/chat?token={Uri.EscapeDataString(token)}",
            UriKind.Absolute);
        var socket = await ConnectWebSocketAsync(uri, cancellationToken);
        if (!_activeChats.TryAdd(request.RequestId, socket))
        {
            socket.Dispose();
            throw new WorkerProtocolException("duplicate_request");
        }

        using var receiveCancellation = new CancellationTokenSource();
        try
        {
            await SendTextAsync(
                socket,
                JsonSerializer.Serialize(new JsonObject
                {
                    ["prompt"] = request.Prompt,
                    ["session_id"] = request.SessionId,
                }),
                cancellationToken);

            long sequence = 0;
            while (true)
            {
                var receive = ReceiveTextAsync(socket, receiveCancellation.Token);
                string json;
                try
                {
                    json = await receive.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await TryCloseOutputAsync(socket);
                    receiveCancellation.Cancel();
                    await IgnoreCancellationAsync(receive);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                var legacy = ParseLegacyFrame(json);
                var type = ReadString(legacy["type"]);
                WorkerEvent? item = type switch
                {
                    "session" => null,
                    "reset" => CreateEvent(
                        request.RequestId,
                        WorkerMessageTypes.ChatReset,
                        sequence,
                        []),
                    "chunk" => CreateEvent(
                        request.RequestId,
                        WorkerMessageTypes.ChatChunk,
                        sequence,
                        new JsonObject { ["text"] = ReadString(legacy["content"]) }),
                    "done" => CreateEvent(
                        request.RequestId,
                        WorkerMessageTypes.ChatDone,
                        sequence,
                        new JsonObject
                        {
                            ["raw_reply"] = ReadString(legacy["content"]),
                            ["session_id"] = ReadOptionalString(legacy["session_id"])
                                ?? request.SessionId,
                        }),
                    "error" => CreateEvent(
                        request.RequestId,
                        WorkerMessageTypes.Error,
                        sequence,
                        new JsonObject
                        {
                            ["code"] = "fastapi_error",
                            ["message"] = ReadOptionalString(legacy["content"])
                                ?? "FastAPI worker failed.",
                            ["retryable"] = false,
                        }),
                    _ => throw new WorkerProtocolException("invalid_worker_response"),
                };

                if (item is null)
                {
                    continue;
                }

                sequence++;
                yield return item;
                if (item.Type is WorkerMessageTypes.ChatDone or WorkerMessageTypes.Error)
                {
                    yield break;
                }
            }
        }
        finally
        {
            receiveCancellation.Cancel();
            RemoveActiveChat(request.RequestId, socket);
            socket.Dispose();
        }
    }

    public Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
        string requestId,
        string prompt,
        string response,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Task.FromException<IReadOnlyList<WorkerFact>>(
            new WorkerProtocolException("unsupported_operation"));
    }

    public Task<VtsCatalog?> GetVtsCatalogAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        WorkerEnvelopeCodec.Validate(new WorkerEnvelope(
            WorkerClientOptions.CurrentProtocolVersion,
            requestId,
            WorkerMessageTypes.VtsCatalog,
            null,
            new JsonObject { ["operation"] = "catalog" }));
        return Task.FromResult<VtsCatalog?>(null);
    }

    public async Task TriggerVtsActionsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(codes);
        var normalized = codes
            .Select(code => code?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToArray();
        if (normalized.Length is < 1 or > 3
            || normalized.Any(code => code.Length is < 1 or > 64)
            || normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new WorkerProtocolException("invalid_request");
        }

        WorkerEnvelopeCodec.Validate(new WorkerEnvelope(
            WorkerClientOptions.CurrentProtocolVersion,
            requestId,
            WorkerMessageTypes.VtsAction,
            null,
            new JsonObject { ["operation"] = "action" }));
        var headers = new Dictionary<string, string>(BuildAuthHeaders(), StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };
        UpstreamResponse response;
        try
        {
            response = await _httpTransport.SendAsync(
                new UpstreamRequest(
                    HttpMethod.Post,
                    "/vts/action",
                    headers,
                    JsonSerializer.SerializeToUtf8Bytes(new JsonObject
                    {
                        ["codes"] = string.Join(' ', normalized),
                    })),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            throw new WorkerUnavailableException();
        }

        var payload = ParseLegacyFrame(Encoding.UTF8.GetString(response.Body));
        if (!string.Equals(
                ReadString(payload["status"]),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerProtocolException("vts_action_failed");
        }
    }

    public async Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_activeChats.TryGetValue(requestId, out var socket)
            && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "cancelled",
                cancellationToken);
        }
    }

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        UpstreamResponse response;
        try
        {
            response = await _httpTransport.SendAsync(
                new UpstreamRequest(
                    HttpMethod.Get,
                    "/status",
                    BuildAuthHeaders(),
                    []),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            throw new WorkerUnavailableException();
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(response.Body)?.AsObject()
                ?? throw new WorkerProtocolException("invalid_worker_response");
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new WorkerProtocolException("invalid_worker_response");
        }

        if (!string.Equals(
                ReadString(payload["status"]),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerUnavailableException();
        }

        return new WorkerHealth(
            true,
            WorkerClientOptions.CurrentProtocolVersion,
            Capabilities);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sockets = _activeChats.Values.Distinct().ToArray();
        _activeChats.Clear();
        foreach (var socket in sockets)
        {
            await TryCloseOutputAsync(socket);
            socket.Dispose();
        }
    }

    private IReadOnlyDictionary<string, string> BuildAuthHeaders()
    {
        var token = _options.Value.DesktopToken?.Trim() ?? string.Empty;
        return token.Length == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token}",
            };
    }

    private async Task<WebSocket> ConnectWebSocketAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _webSocketConnector.ConnectAsync(uri, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
    }

    private static async Task SendTextAsync(
        WebSocket socket,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(value),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
    }

    private async Task<string> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WorkerUnavailableException();
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new WorkerProtocolException("invalid_worker_response");
                }

                var maxBytes = _options.Value.MaxRequestBodyBytes > 0
                    ? _options.Value.MaxRequestBodyBytes
                    : WorkerClientOptions.DefaultMaxMessageBytes;
                if (stream.Length + result.Count > maxBytes)
                {
                    throw new WorkerProtocolException("message_too_large");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (WorkerUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
    }

    private static JsonObject ParseLegacyFrame(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.AsObject()
                ?? throw new WorkerProtocolException("invalid_worker_response");
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new WorkerProtocolException("invalid_worker_response");
        }
    }

    private static WorkerEvent CreateEvent(
        string requestId,
        string type,
        long sequence,
        JsonObject payload) =>
        new(requestId, type, sequence, payload, ReadOnlyMemory<byte>.Empty);

    private static string ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException("invalid_worker_response");
    }

    private static string? ReadOptionalString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw new WorkerProtocolException("invalid_worker_response");
    }

    private static async Task TryCloseOutputAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "cancelled",
                CancellationToken.None);
        }
        catch (Exception error) when (error is WebSocketException
            or ObjectDisposedException
            or InvalidOperationException)
        {
        }
    }

    private static async Task IgnoreCancellationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception error) when (error is OperationCanceledException
            or WorkerUnavailableException
            or WorkerProtocolException)
        {
        }
    }

    private void RemoveActiveChat(string requestId, WebSocket socket)
    {
        if (_activeChats.TryGetValue(requestId, out var current)
            && ReferenceEquals(current, socket))
        {
            _activeChats.TryRemove(requestId, out _);
        }
    }

    private static bool IsTransportFailure(Exception error) =>
        error is WebSocketException
            or HttpRequestException
            or IOException
            or SocketException
            or ObjectDisposedException;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
