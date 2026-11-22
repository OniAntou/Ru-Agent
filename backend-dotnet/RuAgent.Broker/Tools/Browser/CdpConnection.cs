using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// One CDP JSON-RPC connection over a WebSocket (ADR 0055). Requests are correlated by an
/// increasing id; browser events are raised through <see cref="EventReceived"/> with their
/// flattened session id. A closed connection fails every pending request by name.
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private const int MaxErrorDetailChars = 512;
    private const int ReceiveBufferBytes = 64 * 1024;
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiveLoop;
    private int _nextId;
    private int _closed;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public event Action<string, string?, JsonObject?>? EventReceived;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or ArgumentException)
        {
            socket.Dispose();
            throw new BrowserEngineException("browser_launch_failed", "The browser debugger connection failed: " + Bound(error.Message));
        }

        return new CdpConnection(socket);
    }

    public async Task<JsonObject> SendAsync(
        string method,
        JsonObject? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IsClosed) throw new BrowserEngineException("browser_closed", "The browser connection is closed.");
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters.DeepClone();
        if (sessionId is not null) message["sessionId"] = sessionId;
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new BrowserEngineException("browser_cdp_error", "A duplicate CDP request id was generated.");
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        attempt.CancelAfter(timeout);
        try
        {
            var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _sendGate.WaitAsync(attempt.Token).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, attempt.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            using var registration = attempt.Token.Register(
                static state => ((TaskCompletionSource<JsonObject>)state!).TrySetCanceled(),
                completion);
            var response = await completion.Task.ConfigureAwait(false);
            if (response["error"] is JsonObject error)
            {
                var detail = error["message"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : method;
                throw new BrowserEngineException("browser_cdp_error", Bound(detail));
            }

            return response["result"] as JsonObject ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrowserEngineException(
                IsClosed ? "browser_closed" : "browser_timeout",
                IsClosed ? "The browser closed during the action; open it again." : method + " did not answer in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The receive loop already reports its own failure through the closed state.
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var message = new MemoryStream();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Dispatch(message.ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal shutdown path.
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // A broken socket is reported to every waiter by Close below.
        }
        finally
        {
            Close();
        }
    }

    private void Dispatch(byte[] payload)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null) return;
        if (message["id"] is JsonValue idValue
            && idValue.TryGetValue<int>(out var id)
            && _pending.TryGetValue(id, out var completion))
        {
            completion.TrySetResult(message);
            return;
        }

        try
        {
            if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method)) return;
            var sessionId = message["sessionId"] is JsonValue sessionValue
                && sessionValue.TryGetValue<string>(out var session) ? session : null;
            EventReceived?.Invoke(method, sessionId, message["params"] as JsonObject);
        }
        catch (Exception)
        {
            // A subscriber fault must not stop the receive loop.
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            _socket.Abort();
        }
        catch (Exception)
        {
            // Abort is best effort; the cancellation below reports the closure.
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal already cancelled the lifetime.
        }

        var closed = new BrowserEngineException("browser_closed", "The browser connection closed unexpectedly; open the browser again.");
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(closed);
        }

        _pending.Clear();
    }

    private static string Bound(string text) =>
        text.Length <= MaxErrorDetailChars ? text : text[..MaxErrorDetailChars];
}
