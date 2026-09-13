namespace RuApp.Services;

public sealed record BackendSession
{
    public BackendSession(Uri httpBaseUri, string authSecret)
    {
        HttpBaseUri = ValidateHttpBaseUri(httpBaseUri);
        AuthSecret = ValidateSecret(authSecret);
    }

    public Uri HttpBaseUri { get; }
    public string AuthSecret { get; }

    public static BackendSession FromLoopback(int port, string authSecret)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Loopback port must be between 1024 and 65535.");
        }

        return new BackendSession(new Uri($"http://127.0.0.1:{port}/"), authSecret);
    }

    public static BackendSession FromHttpUri(string baseUrl, string authSecret)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Backend URL must be absolute.", nameof(baseUrl));
        }

        return new BackendSession(uri, authSecret);
    }

    private static Uri ValidateHttpBaseUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !uri.IsLoopback
            || uri.Port is < 1024 or > 65535)
        {
            throw new ArgumentException(
                "Backend session must use an HTTP loopback URI with a port between 1024 and 65535.",
                nameof(uri));
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static string ValidateSecret(string authSecret)
    {
        if (string.IsNullOrWhiteSpace(authSecret))
        {
            throw new ArgumentException("Backend session bearer must not be empty.", nameof(authSecret));
        }

        return authSecret;
    }
}
