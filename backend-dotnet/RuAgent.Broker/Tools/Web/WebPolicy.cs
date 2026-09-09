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
        if (uri is null
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
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

    public static bool IsPrivateAddress(IPAddress address) => IsPrivate(address);

    private static bool IsPrivate(IPAddress address)
    {
        // IPv4-mapped IPv6 literals otherwise fall through to the IPv6 checks
        // and could bypass the private IPv4 ranges below.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || IsMulticast(address))
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
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || bytes[0] == 0
                || (bytes[0] == 192 && bytes[1] == 0)
                || (bytes[0] == 198 && bytes[1] is >= 18 and <= 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 240;
        }

        var segments = address.GetAddressBytes();

        // RFC 6052: NAT64 Well-Known Prefix 64:ff9b::/96 embedding private IPv4
        if (segments[0] == 0x00 && segments[1] == 0x64 && segments[2] == 0xff && segments[3] == 0x9b
            && segments[4] == 0 && segments[5] == 0 && segments[6] == 0 && segments[7] == 0
            && segments[8] == 0 && segments[9] == 0 && segments[10] == 0 && segments[11] == 0)
        {
            var embeddedIpv4 = new IPAddress(segments[12..16]);
            if (IsPrivate(embeddedIpv4))
            {
                return true;
            }
        }

        // RFC 3056: 6to4 2002::/16 embedding private IPv4
        if (segments[0] == 0x20 && segments[1] == 0x02)
        {
            var embeddedIpv4 = new IPAddress(segments[2..6]);
            if (IsPrivate(embeddedIpv4))
            {
                return true;
            }
        }

        return (segments[0] & 0xfe) == 0xfc
            || segments[0] == 0xfe && (segments[1] & 0xc0) == 0x80
            || segments[0] == 0xfe && (segments[1] & 0xc0) == 0xc0
            || (segments[0] == 0x01 && segments[1] == 0x00)
            || segments[0] == 0x20 && segments[1] == 0x01
                && segments[2] == 0x0d && segments[3] == 0xb8
            || segments[0] == 0x20 && segments[1] == 0x01
                && segments[2] == 0x00 && segments[3] == 0x00
            || segments[0] == 0x20 && segments[1] == 0x01
                && segments[2] == 0x00 && (segments[3] & 0xf0) == 0x10
            || segments.All(segment => segment == 0);
    }

    private static bool IsMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] is >= 224 and <= 239
            : bytes.Length > 0 && bytes[0] == 0xff;
    }
}
