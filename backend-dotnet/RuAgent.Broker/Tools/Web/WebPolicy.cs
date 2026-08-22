using System.Net;
using System.Net.Sockets;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebPolicy
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public WebPolicy(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _resolve = resolve ?? ((string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));
    }

    public async Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri is null || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new UnauthorizedAccessException("web_uri_not_allowed");
        }

        if (IPAddress.TryParse(uri.Host, out var address))
        {
            RejectPrivate(address);
            return;
        }

        var addresses = await _resolve(uri.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
        {
            throw new UnauthorizedAccessException("web_private_host");
        }
    }

    private static void RejectPrivate(IPAddress address)
    {
        if (IsPrivate(address))
        {
            throw new UnauthorizedAccessException("web_private_host");
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        var segments = address.GetAddressBytes();
        return (segments[0] & 0xfe) == 0xfc || segments[0] == 0xfe && (segments[1] & 0xc0) == 0x80;
    }
}
