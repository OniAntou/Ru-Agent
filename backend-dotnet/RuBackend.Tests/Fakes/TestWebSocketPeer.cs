using System.Net.WebSockets;
using System.Text;

namespace RuBackend.Tests.Fakes;

public sealed class TestWebSocketPeer : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly WebSocket _socket;

    public TestWebSocketPeer(WebSocket socket)
    {
        _socket = socket;
    }

    public async Task SendTextAsync(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(
            payload.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    public async Task SendBinaryAsync(byte[] payload)
    {
        await _socket.SendAsync(
            payload.AsMemory(),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
    }

    public Task CloseOutputAsync()
    {
        return _socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "test complete",
            CancellationToken.None);
    }

    public async Task<string> ReceiveTextAsync()
    {
        var (messageType, payload) = await ReceiveMessageAsync();
        if (messageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException($"Expected a text message, received {messageType}.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    public async Task<byte[]> ReceiveBinaryAsync()
    {
        var (messageType, payload) = await ReceiveMessageAsync();
        if (messageType != WebSocketMessageType.Binary)
        {
            throw new InvalidOperationException($"Expected a binary message, received {messageType}.");
        }

        return payload;
    }

    public async Task<WebSocketCloseStatus?> ReceiveCloseAsync()
    {
        var buffer = new byte[BufferSize];
        var received = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        if (received.MessageType != WebSocketMessageType.Close)
        {
            throw new InvalidOperationException($"Expected a close message, received {received.MessageType}.");
        }

        return _socket.CloseStatus;
    }

    public void Dispose()
    {
        _socket.Dispose();
    }

    private async Task<(WebSocketMessageType MessageType, byte[] Payload)> ReceiveMessageAsync()
    {
        var buffer = new byte[BufferSize];
        await using var message = new MemoryStream();
        WebSocketMessageType? messageType = null;

        while (true)
        {
            var received = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The WebSocket closed before a complete message arrived.");
            }

            messageType ??= received.MessageType;
            if (messageType != received.MessageType)
            {
                throw new WebSocketException("A WebSocket message changed type mid-message.");
            }

            await message.WriteAsync(buffer.AsMemory(0, received.Count));
            if (received.EndOfMessage)
            {
                return (messageType.Value, message.ToArray());
            }
        }
    }
}
