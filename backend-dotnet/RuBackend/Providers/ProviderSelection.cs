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
    /// configuration, and Ollama/LM Studio have no credential account.
    /// </summary>
    public bool RequiresCredential => Alias is "opencode" or "kilo" or "openai" or "anthropic";

    public static ProviderSelection From(SettingsSnapshot settings) => Describe(settings.Backend, settings);

    public static ProviderSelection From(SettingsSnapshot settings, string alias) =>
        Describe(NormalizeAlias(alias), settings);

    private static string NormalizeAlias(string alias) => alias.Trim().ToLowerInvariant() switch
    {
        "opencode" or "opencode zen" or "opencode zen api" or "opencode api" => "OpenCode Zen",
        "kilo" or "kilo gateway" or "kilo gateway api" or "kilo api" => "Kilo Gateway",
        "custom" or "custom openai" or "custom openai api" => "Custom OpenAI",
        "ollama" or "ollama api" => "Ollama",
        "lmstudio" or "lm studio" or "lm studio api" => "LM Studio",
        "openai" or "openai api" => "OpenAI",
        "anthropic" or "anthropic claude" or "claude" => "Anthropic",
        _ => throw new UnknownProviderAliasException(),
    };

    private static ProviderSelection Describe(string backend, SettingsSnapshot settings)
    {
        var (alias, protocol, url, model, account) = backend switch
        {
            "OpenCode Zen" => ("opencode", "chat_completions", "https://opencode.ai/zen/v1", settings.OpenCodeModel, "api_key"),
            "Kilo Gateway" => ("kilo", "chat_completions", "https://api.kilo.ai/api/gateway", settings.KiloModel, "kilo_gateway_api_key"),
            "Custom OpenAI" => ("custom", "chat_completions", settings.CustomUrl, settings.CustomModel, "custom_api_key"),
            "Ollama" => ("ollama", "chat_completions", settings.OllamaUrl, settings.OllamaModel, (string?)null),
            "LM Studio" => ("lmstudio", "chat_completions", settings.LmStudioUrl, settings.LmStudioModel, (string?)null),
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
