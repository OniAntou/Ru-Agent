using System.Security.Cryptography;
using System.Text;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

public sealed record ProviderSelection(string Backend, string Alias, string Protocol, Uri BaseUri, string Model, string? Account)
{
    public Uri ModelsUri => Alias switch
    {
        "openai" => new Uri("https://api.openai.com/v1/models"),
        "anthropic" => new Uri("https://api.anthropic.com/v1/models"),
        _ => new(BaseUri.AbsoluteUri.TrimEnd('/') + "/models"),
    };
    public Uri CompletionUri => Alias switch
    {
        "openai" => new Uri("https://api.openai.com/v1/responses"),
        "anthropic" => new Uri("https://api.anthropic.com/v1/messages"),
        _ => new(BaseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions"),
    };
    public string PreferenceKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Backend}\n{BaseUri.AbsoluteUri}\n{Model}")));

    /// <summary>
    /// Cloud providers that require a signed-in credential. A missing credential makes
    /// their catalog fail closed, so no picker offers a model that cannot run. Custom
    /// OpenAI stays exempt because a keyless local endpoint is a supported
    /// configuration.
    /// </summary>
    public bool RequiresCredential => Alias is "openai" or "anthropic" || IsModelsDev;

    public bool IsModelsDev => IsModelsDevBackend(Backend);

    public static bool IsModelsDevBackend(string? backend) =>
        NormalizeModelsDevBackend(backend) is not null;

    /// <summary>
    /// Canonical <c>models.dev:&lt;provider-id&gt;</c> form, or null when the value
    /// is not a generic backend. The id is restricted to a bounded slug so it can
    /// safely derive credential account names.
    /// </summary>
    public static string? NormalizeModelsDevBackend(string? value)
    {
        const string prefix = "models.dev:";
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = trimmed[prefix.Length..].Trim().ToLowerInvariant();
        if (id.Length == 0 || id.Length > 64
            || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        return prefix + id;
    }

    public static ProviderSelection From(SettingsSnapshot settings) => Describe(settings.Backend, settings);

    public static ProviderSelection From(SettingsSnapshot settings, string alias) =>
        Describe(NormalizeAlias(alias), settings);

    /// <summary>
    /// Generic <c>models.dev:&lt;provider-id&gt;</c> backends resolve their base URL
    /// from the Models.dev catalog. Unknown ids fail closed: the URL is never guessed.
    /// </summary>
    public static ProviderSelection From(SettingsSnapshot settings, IReadOnlyList<ModelsDevProvider> modelsDevCatalog) =>
        Describe(settings.Backend, settings, modelsDevCatalog);

    private static string NormalizeAlias(string alias) => alias.Trim().ToLowerInvariant() switch
    {
        "opencode" or "opencode zen" or "opencode zen api" or "opencode api" or "gguf" or "llama.cpp" => "models.dev:opencode",
        "kilo" or "kilo gateway" or "kilo gateway api" or "kilo api" => "models.dev:kilo",
        "custom" or "custom openai" or "custom openai api" => "Custom OpenAI",
        "openai" or "openai api" => "OpenAI",
        "anthropic" or "anthropic claude" or "claude" => "Anthropic",
        _ when NormalizeModelsDevBackend(alias) is { } generic => generic,
        _ => throw new UnknownProviderAliasException(),
    };

    private static ProviderSelection Describe(string backend, SettingsSnapshot settings, IReadOnlyList<ModelsDevProvider>? modelsDevCatalog = null)
    {
        if (NormalizeModelsDevBackend(backend) is { } generic)
        {
            const string prefix = "models.dev:";
            var id = generic[prefix.Length..];
            var provider = modelsDevCatalog?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (provider is null
                || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var providerUri)
                || providerUri.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException("Unknown models.dev provider.");
            }

            var genericModel = settings.Model?.Trim() ?? string.Empty;
            return new(generic, provider.Id, "chat_completions", providerUri, genericModel, provider.Id.ToLowerInvariant() + "_api_key");
        }

        var (alias, protocol, url, model, account) = backend switch
        {
            "Custom OpenAI" => ("custom", "chat_completions", settings.CustomUrl, settings.CustomModel, "custom_api_key"),
            "OpenAI" => ("openai", "openai_responses", "https://api.openai.com/v1", settings.OpenAiModel, "openai_api_key"),
            "Anthropic" => ("anthropic", "anthropic_messages", "https://api.anthropic.com/v1", settings.AnthropicModel, "anthropic_api_key"),
            _ => throw new ArgumentException("Unsupported model provider."),
        };
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Invalid provider endpoint.");
        model = model?.Trim() ?? string.Empty;
        return new(backend, alias, protocol, uri, model, account);
    }
}

public sealed class UnknownProviderAliasException : Exception
{
    public UnknownProviderAliasException()
        : base("Unsupported model provider alias.")
    {
    }
}
