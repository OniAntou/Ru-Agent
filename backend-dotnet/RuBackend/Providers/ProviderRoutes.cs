using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Providers;

/// <summary>What Ru adds to one Models.dev provider entry (ADR 0052).</summary>
/// <param name="Id">Models.dev provider id.</param>
/// <param name="BaseUrl">
/// The endpoint root when Models.dev publishes none: it leaves <c>api</c> out for
/// providers whose own SDK knows the URL. Never used to replace a published one.
/// </param>
/// <param name="Protocol">A wire protocol that overrides the one the entry's <c>npm</c> implies.</param>
/// <param name="ModelsFromCatalog">The provider serves no usable <c>GET {base}/models</c>, so its model list comes from Models.dev.</param>
internal sealed record ProviderRoute(
    string Id,
    string? BaseUrl = null,
    string? Protocol = null,
    bool ModelsFromCatalog = false);

/// <summary>
/// The provider route table: which wire protocol and endpoint each Models.dev provider
/// is reached with (ADR 0052).
/// </summary>
/// <remarks>
/// Models.dev describes every provider with the AI SDK package that serves it
/// (<c>npm</c>), and that package fixes the protocol. Ru used to read only <c>api</c> and
/// send everything as chat completions, so a provider published with
/// <c>@ai-sdk/anthropic</c> (MiniMax, among others) was called at
/// <c>…/anthropic/v1/chat/completions</c> and answered 404, and every provider without
/// an <c>api</c> (xAI, Groq, Mistral, …) was dropped from the list.
///
/// The protocol is derived once, when the catalog is read, and then pinned with the run
/// like any other selection; nothing here switches a running selection. Providers whose
/// only route needs credentials other than an API key (Bedrock, Vertex, Azure, watsonx,
/// SAP, GitLab) stay out, and so do Models.dev's own <c>openai</c> and <c>anthropic</c>
/// entries: their credential accounts would collide with Ru's first-party providers.
/// </remarks>
internal static class ProviderRoutes
{
    /// <summary>
    /// Endpoints for providers Models.dev publishes without an <c>api</c>. Each was
    /// live-probed without a credential on 2026-11-19 and answered 401/403 (or listed
    /// models) at <c>{base}/models</c>; Google answers 404 there without a key, so its list
    /// comes from Models.dev. Perplexity is left out: none of its models call tools, and
    /// every Ru run needs them.
    /// </summary>
    private static readonly Dictionary<string, ProviderRoute> Table = new ProviderRoute[]
    {
        new("xai", "https://api.x.ai/v1"),
        new("groq", "https://api.groq.com/openai/v1"),
        new("mistral", "https://api.mistral.ai/v1"),
        new("google", "https://generativelanguage.googleapis.com/v1beta/openai", ModelsFromCatalog: true),
        new("cerebras", "https://api.cerebras.ai/v1"),
        new("togetherai", "https://api.together.xyz/v1"),
        new("deepinfra", "https://api.deepinfra.com/v1/openai"),
        new("cohere", "https://api.cohere.ai/compatibility/v1"),
        new("venice", "https://api.venice.ai/api/v1"),
    }.ToDictionary(route => route.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Models.dev entries Ru never offers: their accounts are Ru's first-party ones.</summary>
    private static readonly HashSet<string> Reserved = new(["openai", "anthropic"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The protocol and endpoint for one Models.dev entry, or null when Ru cannot reach it.
    /// </summary>
    /// <param name="id">The Models.dev provider id.</param>
    /// <param name="npm">The entry's <c>npm</c> package, if any.</param>
    /// <param name="publishedApi">The entry's <c>api</c>, already checked to be an https URL, or null.</param>
    internal static (string Protocol, string BaseUrl)? Resolve(string id, string? npm, string? publishedApi)
    {
        if (Reserved.Contains(id)) return null;
        Table.TryGetValue(id, out var route);
        var baseUrl = publishedApi ?? route?.BaseUrl;
        if (baseUrl is null) return null;
        var protocol = route?.Protocol ?? ProtocolForPackage(npm);
        return protocol is null ? null : (protocol, baseUrl);
    }

    /// <summary>
    /// The wire protocol an AI SDK package speaks. Packages that are only an
    /// OpenAI-compatible client under another name keep chat completions; a package Ru
    /// cannot speak returns null and its provider is not offered.
    /// </summary>
    internal static string? ProtocolForPackage(string? npm) => npm?.Trim() switch
    {
        "@ai-sdk/anthropic" => ProviderProtocols.AnthropicMessages,
        "@ai-sdk/amazon-bedrock" or "@ai-sdk/google-vertex" or "@ai-sdk/google-vertex/anthropic"
            or "@ai-sdk/azure" => null,
        // Everything else Models.dev lists with an api root is served over chat completions
        // today; keeping it so is what keeps an existing selection working.
        _ => ProviderProtocols.ChatCompletions,
    };

    /// <summary>
    /// Whether a provider's model list comes from Models.dev instead of
    /// <c>GET {base}/models</c>: providers without a usable list, and every
    /// Anthropic-protocol gateway, whose list route and auth vary by vendor.
    /// </summary>
    internal static bool UsesModelsDevCatalog(string providerId, string protocol) =>
        ProviderOverrides.UsesModelsDevCatalog(providerId)
        || protocol == ProviderProtocols.AnthropicMessages
        || (Table.TryGetValue(providerId, out var route) && route.ModelsFromCatalog);
}
