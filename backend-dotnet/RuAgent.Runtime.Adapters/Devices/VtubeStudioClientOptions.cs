namespace RuAgent.Runtime.Adapters.Devices;

public sealed record VtubeStudioClientOptions
{
    public VtubeStudioClientOptions(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || !endpoint.IsLoopback
            || endpoint.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("VTube Studio endpoint must be loopback WebSocket.", nameof(endpoint));
        }

        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public string PluginName { get; init; } = "RuApp";

    public string PluginDeveloper { get; init; } = "Antigravity";

    public string? TokenPath { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxMessageBytes { get; init; } = 512 * 1024;

    public void Validate()
    {
        if (!IsSafeText(PluginName, 128) || !IsSafeText(PluginDeveloper, 128))
        {
            throw new ArgumentException("VTube Studio plugin identity is invalid.");
        }

        if (TokenPath is not null && (string.IsNullOrWhiteSpace(TokenPath) || TokenPath.Length > 4_096))
        {
            throw new ArgumentException("VTube Studio token path is invalid.", nameof(TokenPath));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaxMessageBytes is < 256 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        }
    }

    private static bool IsSafeText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && !value.Any(char.IsControl);
}
