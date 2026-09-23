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
