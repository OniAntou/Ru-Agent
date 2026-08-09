using System.Net.WebSockets;
using RuBackend.Compatibility;

namespace RuBackend.Tests.Fakes;

public sealed class StubUpstreamWebSocketConnector : IUpstreamWebSocketConnector
{
    private WebSocket? _socket;

    public Uri? LastRequestedUri { get; private set; }

    public StubUpstreamWebSocketConnector(WebSocket socket)
    {
        _socket = socket;
    }

    public Task<WebSocket> ConnectAsync(Uri upstreamUri, CancellationToken cancellationToken)
    {
        LastRequestedUri = upstreamUri;
        var socket = Interlocked.Exchange(ref _socket, null)
            ?? throw new InvalidOperationException("The stub upstream WebSocket was already consumed.");
        return Task.FromResult(socket);
    }
}
