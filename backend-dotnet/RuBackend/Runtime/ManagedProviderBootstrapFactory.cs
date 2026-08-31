using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business.Settings;

namespace RuBackend.Runtime;

/// <summary>
/// Converts the trusted persisted model selection into one short-lived protected
/// bootstrap consumed by the managed Agent worker. Provider credentials never
/// cross the process boundary as environment variables or public metadata.
/// </summary>
public sealed class ManagedProviderBootstrapFactory
{
    private const string CredentialService = "Ru";
    private const string OpenCodeEndpoint = "https://opencode.ai/zen/v1";
    private const string KiloEndpoint = "https://api.kilo.ai/api/gateway";

    public async Task<string> CreateAsync(
        string sessionRoot,
        SettingsSnapshot settings,
        ICredentialStore credentials,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);

        var selection = SelectProvider(settings);
        // A missing key stays missing. Never borrow an alias or another provider's key.
        var credential = selection.CredentialAccount is null
            ? string.Empty
            : await credentials.GetAsync(
                CredentialService,
                selection.CredentialAccount,
                cancellationToken).ConfigureAwait(false) ?? string.Empty;

        var data = new ProviderBootstrapData(
            selection.Backend,
            selection.Endpoint,
            selection.Model,
            credential);
        return ProtectedProviderBootstrap.Write(sessionRoot, data);
    }

    private static ProviderSelection SelectProvider(SettingsSnapshot settings) =>
        settings.Backend switch
        {
            "OpenCode Zen" => new ProviderSelection(
                settings.Backend,
                CompletionEndpoint(OpenCodeEndpoint),
                FirstNonEmpty(settings.OpenCodeModel, settings.Model, "deepseek-v4-flash-free"),
                "api_key"),
            "Kilo Gateway" => new ProviderSelection(
                settings.Backend,
                CompletionEndpoint(KiloEndpoint),
                FirstNonEmpty(settings.KiloModel, settings.Model, "deepseek-ai/DeepSeek-V3"),
                "kilo_gateway_api_key"),
            "Ollama" => new ProviderSelection(
                settings.Backend,
                CompletionEndpoint(settings.OllamaUrl),
                FirstNonEmpty(settings.OllamaModel, settings.Model, "qwen2.5:latest"),
                null),
            "LM Studio" => new ProviderSelection(
                settings.Backend,
                CompletionEndpoint(settings.LmStudioUrl),
                FirstNonEmpty(settings.LmStudioModel, settings.Model, "default"),
                null),
            "Custom OpenAI" => new ProviderSelection(
                settings.Backend,
                CompletionEndpoint(settings.CustomUrl),
                FirstNonEmpty(settings.CustomModel, settings.Model, "default"),
                "custom_api_key"),
            _ => throw new ArgumentException("Unsupported model backend.", nameof(settings)),
        };

    private static Uri CompletionEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Provider endpoint is required.", nameof(baseUrl));
        }

        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("Provider endpoint is invalid.", nameof(baseUrl));
        }

        return new Uri(normalized + "/chat/completions", UriKind.Absolute);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? throw new ArgumentException("Provider model is required.");

    private sealed record ProviderSelection(
        string Backend,
        Uri Endpoint,
        string Model,
        string? CredentialAccount);
}
