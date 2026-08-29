using System.Net.WebSockets;

namespace RuAgent.Runtime.Adapters.Devices;

public readonly record struct VtubeStudioWebSocketFrame(
    WebSocketMessageType MessageType,
    bool EndOfMessage,
    ReadOnlyMemory<byte> Payload);

public interface IVtubeStudioWebSocket : IAsyncDisposable
{
    Task SendTextAsync(string message, CancellationToken cancellationToken);

    ValueTask<VtubeStudioWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IVtubeStudioWebSocketFactory
{
    Task<IVtubeStudioWebSocket> ConnectAsync(
        Uri endpoint,
        CancellationToken cancellationToken);
}

public sealed class ClientWebSocketFactory : IVtubeStudioWebSocketFactory
{
    public async Task<IVtubeStudioWebSocket> ConnectAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new ClientWebSocketAdapter(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class ClientWebSocketAdapter : IVtubeStudioWebSocket
{
    private readonly ClientWebSocket _socket;
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];

    public ClientWebSocketAdapter(ClientWebSocket socket) => _socket = socket;

    public Task SendTextAsync(string message, CancellationToken cancellationToken) =>
        _socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

    public async ValueTask<VtubeStudioWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(_receiveBuffer, cancellationToken).ConfigureAwait(false);
        return new VtubeStudioWebSocketFrame(
            result.MessageType,
            result.EndOfMessage,
            _receiveBuffer.AsMemory(0, result.Count).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "shutdown",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
    }
}
