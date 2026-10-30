namespace RuAgent.Runtime.Adapters.Bootstrap;

/// <summary>
/// How Ru names itself to a gateway that asks clients to identify. OpenCode Go
/// asks for a client-owned User-Agent and routes each conversation by a stable id;
/// both travel as ordinary request headers on every generation.
/// </summary>
public static class ProviderWireIdentity
{
    public const string UserAgentHeaderName = "User-Agent";

    /// <summary>Ru's own product name, not a generic HTTP-library default.</summary>
    public static readonly string UserAgent = "RuAgent/" + (
        typeof(ProviderWireIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    /// <summary>
    /// The header set for a provider that pins a conversation header: the stable
    /// session id it routes by, plus Ru's identity.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForSession(string headerName, string sessionId) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [headerName] = sessionId,
            [UserAgentHeaderName] = UserAgent,
        };
}
