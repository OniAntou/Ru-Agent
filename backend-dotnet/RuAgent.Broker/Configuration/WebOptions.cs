using System.Net;
using RuAgent.Broker.Tools.Web;

namespace RuAgent.Broker.Configuration;

public sealed record WebOptions
{
    public const int DefaultMaxResponseBytes = 256 * 1024;
    public const int MaxConfiguredResponseBytes = 4 * 1024 * 1024;

    public WebOptions(
        bool enabled = false,
        Uri? searchEndpoint = null,
        int maxResponseBytes = DefaultMaxResponseBytes)
    {
        if (maxResponseBytes is < 1 or > MaxConfiguredResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        if (searchEndpoint is not null)
        {
            if (searchEndpoint.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(searchEndpoint.Host)
                || !string.IsNullOrEmpty(searchEndpoint.UserInfo)
                || IsPrivateHost(searchEndpoint.Host))
            {
                throw new ArgumentException("web_search_endpoint_not_allowed", nameof(searchEndpoint));
            }
        }

        Enabled = enabled;
        SearchEndpoint = searchEndpoint;
        MaxResponseBytes = maxResponseBytes;
    }

    public bool Enabled { get; }
    public Uri? SearchEndpoint { get; }
    public int MaxResponseBytes { get; }

    public bool FetchEnabled => Enabled;
    public bool SearchEnabled => Enabled && SearchEndpoint is not null;

    private static bool IsPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address)
            && WebPolicy.IsPrivateAddress(address);
    }
}
