using System.Net.WebSockets;

namespace RuBackend.Compatibility;

public interface IUpstreamWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri upstreamUri, CancellationToken cancellationToken);
}
