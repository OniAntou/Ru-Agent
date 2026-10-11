using System.Net;
using RuAgent.Broker.Tools.Web;

namespace RuAgent.Broker.Configuration;

public sealed record WebOptions
{
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxConfiguredResponseBytes = 4 * 1024 * 1024;

    public static readonly Uri DefaultGoogleEndpoint = new("https://www.google.com/search");

    /// <summary>
    /// Free fallback engines raced against the configured primary; the first
    /// engine with usable results answers. Fixed public services (no keys);
    /// every request still passes WebPolicy, so a fallback never widens the
    /// network boundary the call was granted under.
    /// </summary>
    public static readonly IReadOnlyList<Uri> DefaultFallbackEndpoints = new[]
    {
        new Uri("https://html.duckduckgo.com/html/"),
        new Uri("https://www.bing.com/search"),
        new Uri("https://www.mojeek.com/search"),
    };

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
        // The primary engine is whatever is configured (Google by default).
        // Free fallback engines (DuckDuckGo, Bing, Mojeek) race it; the first
        // engine with usable results answers. The set is fixed public
        // services and every request still passes WebPolicy, so a fallback
        // never widens the network boundary the call was granted under.
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
