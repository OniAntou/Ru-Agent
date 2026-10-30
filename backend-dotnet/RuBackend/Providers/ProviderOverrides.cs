namespace RuBackend.Providers;

/// <summary>
/// Per-provider overrides for Models.dev entries the generic
/// <c>chat_completions</c> path cannot serve. The table stays tiny on purpose:
/// each entry needs a live-probed reason (405, templated URL, special auth),
/// not a guess.
/// </summary>
internal static class ProviderOverrides
{
    internal const string CloudflareId = "cloudflare-workers-ai";

    /// <summary>OpenCode Zen, whose chat root is <c>/inference/openai/v1</c>.</summary>
    internal const string OpencodeId = "opencode";

    /// <summary>OpenCode Go, a separately billed gateway under <c>/inference/go</c>.</summary>
    internal const string OpencodeGoId = "opencode-go";

    /// <summary>
    /// The host OpenCode's gateways live on. The catalog override is applied only
    /// when the resolved base URL still points here, so a stale table entry can
    /// never send a key to a provider that merely happens to share the id.
    /// </summary>
    internal const string OpencodeHost = "opencode.ai";

    /// <summary>
    /// OpenCode's model list sits beside the protocol root, not under it:
    /// <c>{api}/models</c> answers 404 for both gateways, while
    /// <c>/inference/v1/models</c> (Zen) and <c>/inference/go/v1/models</c> (Go)
    /// answer the live list and reject a bad key with 401. Appending "/models"
    /// to the published chat base — what every other provider accepts — cannot work.
    /// </summary>
    internal const string OpencodeCatalogBaseUrl = "https://opencode.ai/inference/v1";
    internal const string OpencodeGoCatalogBaseUrl = "https://opencode.ai/inference/go/v1";

    /// <summary>
    /// OpenCode Go rejects every completion that carries no conversation id
    /// (HTTP 400 <c>MissingSessionID</c>) before it looks at the key, so the
    /// header is pinned with the provider and sent on each request.
    /// </summary>
    internal const string OpencodeSessionHeader = "x-opencode-session";

    /// <summary>
    /// Workers AI OpenAI-compatible root. The catalog's <c>api</c> carries a
    /// <c>${CLOUDFLARE_ACCOUNT_ID}</c> template variable, so the account id is
    /// filled in from settings at resolve time, never guessed or defaulted.
    /// </summary>
    internal const string CloudflareUrlTemplate =
        "https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1";

    internal static bool IsOverridden(string? providerId) =>
        string.Equals(providerId?.Trim(), CloudflareId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this provider's catalog comes from the Models.dev model list
    /// instead of a <c>GET {base}/models</c> fetch (Cloudflare answers 405).
    /// </summary>
    internal static bool UsesModelsDevCatalog(string? providerId) => IsOverridden(providerId);

    /// <summary>
    /// The base URL to append <c>/models</c> to, or null when the provider's
    /// published <c>api</c> serves its own catalog. Only a provider whose
    /// resolved endpoint still points at OpenCode is redirected; the table is
    /// keyed by provider id, never by URL shape.
    /// </summary>
    internal static string? CatalogBaseUrl(string? providerId, Uri? baseUri)
    {
        if (baseUri is null
            || !string.Equals(baseUri.Host, OpencodeHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return providerId?.Trim().ToLowerInvariant() switch
        {
            OpencodeId => OpencodeCatalogBaseUrl,
            OpencodeGoId => OpencodeGoCatalogBaseUrl,
            _ => null,
        };
    }

    /// <summary>
    /// The conversation-id header this provider demands, or null when it sends none.
    /// </summary>
    internal static string? SessionHeaderName(string? providerId) =>
        string.Equals(providerId?.Trim(), OpencodeGoId, StringComparison.OrdinalIgnoreCase)
            ? OpencodeSessionHeader
            : null;

    /// <summary>
    /// Fills the Cloudflare base URL. An absent or malformed account id throws:
    /// callers map it to a configuration error, never to a guessed URL.
    /// </summary>
    internal static string CloudflareBaseUrl(string? accountId)
    {
        var id = (accountId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > 64
            || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ArgumentException("Cloudflare account id is missing or invalid.");
        }

        return CloudflareUrlTemplate.Replace("{account_id}", id, StringComparison.Ordinal);
    }
}
