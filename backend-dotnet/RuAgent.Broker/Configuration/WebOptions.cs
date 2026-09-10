using System.Net;
using RuAgent.Broker.Tools.Web;

namespace RuAgent.Broker.Configuration;

public sealed record WebOptions
{
    public const int DefaultMaxResponseBytes = 256 * 1024;
    public const int MaxConfiguredResponseBytes = 4 * 1024 * 1024;

    public static readonly Uri DefaultGoogleEndpoint = new("https://www.google.com/search");
    public static readonly Uri DefaultFallbackEndpoint = new("https://html.duckduckgo.com/html/");

    public WebOptions(
        bool enabled = false,
        Uri? searchEndpoint = null,
        Uri? fallbackSearchEndpoint = null,
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

        if (fallbackSearchEndpoint is not null)
        {
            ValidatePublicEndpoint(fallbackSearchEndpoint, nameof(fallbackSearchEndpoint));
        }

        Enabled = enabled;
        SearchEndpoint = searchEndpoint ?? (enabled ? DefaultGoogleEndpoint : null);
        FallbackSearchEndpoint = fallbackSearchEndpoint ?? (enabled ? DefaultFallbackEndpoint : null);
        MaxResponseBytes = maxResponseBytes;
    }

    public bool Enabled { get; }
    public Uri? SearchEndpoint { get; }
    public Uri? FallbackSearchEndpoint { get; }
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
