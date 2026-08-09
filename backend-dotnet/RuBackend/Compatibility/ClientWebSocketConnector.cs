using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Compatibility;

public sealed class ClientWebSocketConnector : IUpstreamWebSocketConnector
{
    private readonly IOptions<BackendOptions> _options;

    public ClientWebSocketConnector(IOptions<BackendOptions> options)
    {
        _options = options;
    }

    public async Task<WebSocket> ConnectAsync(Uri upstreamUri, CancellationToken cancellationToken)
    {
        var configuredBase = new Uri(
            _options.Value.UpstreamUrl.TrimEnd('/') + "/",
            UriKind.Absolute);
        var target = new UriBuilder(configuredBase)
        {
            Scheme = configuredBase.Scheme.ToLowerInvariant() switch
            {
                "https" or "wss" => "wss",
                "http" or "ws" => "ws",
                _ => throw new InvalidOperationException("RuBackend:UpstreamUrl must use http, https, ws, or wss."),
            },
            Path = upstreamUri.AbsolutePath,
            Query = upstreamUri.Query.TrimStart('?'),
        }.Uri;

        var client = new ClientWebSocket();
        try
        {
            await client.ConnectAsync(target, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
