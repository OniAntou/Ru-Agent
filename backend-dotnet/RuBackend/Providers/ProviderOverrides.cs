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

    /// <summary>OpenRouter, whose model list answers anyone, keyed or not.</summary>
    internal const string OpenRouterId = "openrouter";
    internal const string OpenRouterHost = "openrouter.ai";
    internal const string CloudflareHost = "api.cloudflare.com";

    /// <summary>xAI, which answers a wrong key with 400 rather than 401 (live-probed 2026-11-19).</summary>
    internal const string XaiId = "xai";
    internal const string XaiHost = "api.x.ai";

    /// <summary>
    /// An authenticated endpoint that proves a key, for a provider whose catalog
    /// cannot: OpenRouter's <c>/models</c> answers 200 to a made-up key, and
    /// Cloudflare's catalog comes from Models.dev, so the key is never sent. Each
    /// answers 401 for a wrong key, and Cloudflare 400 for a malformed one (live-probed
    /// 2026-11-19). Null for every other
    /// provider; the URL is only built while the resolved base still points at the
    /// provider's own host, so a reused id never receives the key.
    /// </summary>
    internal static Uri? KeyCheckUri(ProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsModelsDev) return null;
        var host = selection.BaseUri.Host;
        if (string.Equals(selection.Alias, OpenRouterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(host, OpenRouterHost, StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("https://openrouter.ai/api/v1/key");
        }

        if (string.Equals(selection.Alias, XaiId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(host, XaiHost, StringComparison.OrdinalIgnoreCase))
        {
            // Its list answers 400 "Incorrect API key" to a wrong key, which the catalog
            // path reads as an outage; the key endpoint gives the same 400 and 200 for a
            // working key, and the key check reads 400 as a refusal.
            return new Uri("https://api.x.ai/v1/api-key");
        }

        if (string.Equals(selection.Alias, CloudflareId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(host, CloudflareHost, StringComparison.OrdinalIgnoreCase))
        {
            // {base} is .../accounts/{id}/ai/v1; the account's model search sits at .../ai/models/search.
            var root = selection.BaseUri.AbsoluteUri.TrimEnd('/');
            if (!root.EndsWith("/ai/v1", StringComparison.OrdinalIgnoreCase)) return null;
            return new Uri(root[..^"/v1".Length] + "/models/search?per_page=1");
        }

        return null;
    }

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
