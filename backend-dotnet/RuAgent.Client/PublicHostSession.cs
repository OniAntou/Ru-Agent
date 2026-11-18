using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed record PublicHostSession(
    Uri BaseUri,
    string BearerToken,
    RuntimeDescriptor Descriptor)
{
    public static PublicHostSession FromDescriptor(RuntimeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("BaseUrl invalid.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.BearerToken))
            throw new ArgumentException("BearerToken missing.", nameof(descriptor));
        var normalized = new UriBuilder(uri) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
        return new PublicHostSession(normalized, descriptor.BearerToken, descriptor);
    }

    /// <summary>
    /// The compiler-generated record text would print the bearer and the descriptor (which
    /// carries it too); any log line or exception that formats a session must not leak it.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("BaseUri = ").Append(BaseUri).Append(", BearerToken = <redacted>");
        return true;
    }

    public Uri BuildUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'", nameof(path));
        return new Uri(BaseUri, path.TrimStart('/'));
    }
}
