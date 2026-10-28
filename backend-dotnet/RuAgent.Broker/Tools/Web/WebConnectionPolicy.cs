using System.Net;
using System.Net.Sockets;

namespace RuAgent.Broker.Tools.Web;

/// <summary>
/// Connect-time enforcement for web tools: the socket opens against the exact
/// address that passed the private-range checks, so a DNS answer that changes
/// between validation and connection (rebinding) cannot redirect the request
/// to a private target.
/// </summary>
public static class WebConnectionPolicy
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var resolver = resolve
            ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));
        return (context, cancellationToken) => OpenValidatedConnectionAsync(
            resolver,
            context.DnsEndPoint.Host,
            context.DnsEndPoint.Port,
            cancellationToken);
    }

    public static SocketsHttpHandler CreateHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            // Decompression happens before WebResponseReader counts bytes, so its
            // download bound applies to the decoded body and a small compressed
            // response cannot expand past it.
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = CreateConnectCallback(resolve),
        };

    public static async ValueTask<Stream> OpenValidatedConnectionAsync(
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
        var allowed = SelectPublicAddresses(addresses);
        Exception? failure = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket
                    .ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error)
            {
                socket.Dispose();
                if (error is OperationCanceledException)
                {
                    throw;
                }

                failure = error;
            }
        }

        throw new HttpRequestException("web_connect_failed", failure);
    }

    public static IPAddress[] SelectPublicAddresses(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var allowed = addresses
            .Where(address => !WebPolicy.IsPrivateAddress(address))
            .ToArray();
        if (allowed.Length == 0)
        {
            throw new UnauthorizedAccessException("web_private_host");
        }

        return allowed;
    }
}
