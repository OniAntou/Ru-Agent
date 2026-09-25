using System.Net;
using RuAgent.Broker.Tools.Web;

namespace RuAgent.Broker.Configuration;

public sealed record WebOptions
{
    public const int DefaultMaxResponseBytes = 256 * 1024;
    public const int MaxConfiguredResponseBytes = 4 * 1024 * 1024;

    public static readonly Uri DefaultGoogleEndpoint = new("https://www.google.com/search");

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
            ValidatePublicEndpoint(searchEndpoint, nameof(searchEndpoint));
        }

        Enabled = enabled;
        // Exactly one search engine, whatever it is configured to be. A second
        // engine would be a provider fallback: the query would reach a service the
        // user never configured, and the destination would sit outside the approval
        // the call was granted under. A primary engine that fails or returns nothing
        // fails closed with its own bounded code.
        SearchEndpoint = searchEndpoint ?? (enabled ? DefaultGoogleEndpoint : null);
        MaxResponseBytes = maxResponseBytes;
    }

    public bool Enabled { get; }
    public Uri? SearchEndpoint { get; }
    public int MaxResponseBytes { get; }

    public bool FetchEnabled => Enabled;
    public bool SearchEnabled => Enabled && SearchEndpoint is not null;

    private static void ValidatePublicEndpoint(Uri endpoint, string parameterName)
    {
        if (endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || IsPrivateHost(endpoint.Host))
        {
            throw new ArgumentException("web_search_endpoint_not_allowed", parameterName);
        }
    }

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
