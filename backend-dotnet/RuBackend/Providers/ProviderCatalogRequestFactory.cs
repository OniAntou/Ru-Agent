using System.Net.Http.Headers;

namespace RuBackend.Providers;

public static class ProviderCatalogRequestFactory
{
    public const string AnthropicVersion = "2023-06-01";

    public static HttpRequestMessage Create(ProviderSelection selection, string credential)
    {
        ArgumentNullException.ThrowIfNull(selection);
        // Custom endpoints are user-chosen: a key never travels over plain HTTP to another host (ADR 0034).
        if (selection.Alias == "custom" || selection.IsCustomProvider)
            CustomProviders.RequireCredentialTransport(selection.ModelsUri, credential);
        var request = new HttpRequestMessage(HttpMethod.Get, selection.ModelsUri);
        if (selection.IsCustomProvider)
        {
            if (credential.Length == 0) return request;
            if (selection.Protocol == RuAgent.Runtime.Adapters.Bootstrap.ProviderProtocols.AnthropicMessages)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", RequireCredential(credential));
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireCredential(credential));
            }
        }
        else if (selection.Alias == "openai")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireCredential(credential));
        else if (selection.Alias == "anthropic")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", RequireCredential(credential));
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else if (selection.Alias == "custom" && credential.Length != 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        else if (selection.IsModelsDev && credential.Length != 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static string RequireCredential(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 4096 || credential.Any(char.IsControl))
            throw new ArgumentException("Provider credential is invalid.", nameof(credential));
        return credential;
    }
}
