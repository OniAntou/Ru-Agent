using System.Buffers;
using System.Net.WebSockets;

namespace RuBackend.Compatibility;

public sealed class WebSocketRelay
{
    private const int BufferSize = 64 * 1024;

    public async Task RelayAsync(
        WebSocket client,
        WebSocket upstream,
        CancellationToken cancellationToken)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToUpstream = RelayDirectionAsync(client, upstream, relayCancellation.Token);
        var upstreamToClient = RelayDirectionAsync(upstream, client, relayCancellation.Token);
        var directions = new[] { clientToUpstream, upstreamToClient };

        try
        {
            await Task.WhenAny(directions);
        }
        finally
        {
            relayCancellation.Cancel();
            try
            {
                await Task.WhenAll(directions);
            }
            catch (OperationCanceledException)
            {
            }

            await CloseOutputAsync(client);
            await CloseOutputAsync(upstream);
        }
    }

    private static async Task RelayDirectionAsync(
        WebSocket source,
        WebSocket target,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (source.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var received = await source.ReceiveAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await CloseOutputAsync(target, source.CloseStatus, source.CloseStatusDescription);
                    return;
                }

                await target.SendAsync(
                    buffer.AsMemory(0, received.Count),
                    received.MessageType,
                    received.EndOfMessage,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task CloseOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus? closeStatus = null,
        string? statusDescription = null)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return Task.CompletedTask;
        }

        return CloseOutputCoreAsync(socket, closeStatus, statusDescription);
    }

    private static async Task CloseOutputCoreAsync(
        WebSocket socket,
        WebSocketCloseStatus? closeStatus,
        string? statusDescription)
    {
        try
        {
            await socket.CloseOutputAsync(
                closeStatus ?? WebSocketCloseStatus.NormalClosure,
                statusDescription,
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
