using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// What the app knows about each provider's setup, in one place.
/// </summary>
/// <remarks>
/// The provider set already exists in three forms across the codebase: the alias
/// the backend stores, the human name shown in the picker, and the credential
/// field that says whether a key is present. Nothing mapped them together, so the
/// app could not answer the question a new user actually has — "which provider is
/// missing a key, and where do I get one".
///
/// This is that single table. It is deliberately pure: it takes the settings
/// payload and answers questions about it, with no network, no disk, and no XAML,
/// so every branch is asserted in tests instead of being read out of a view.
/// </remarks>
internal static class ProviderSetupGuidance
{
    /// <summary>The provider the app uses when the user has never chosen one.</summary>
    internal const string DefaultBackendChoice = "opencode";

    internal sealed record ProviderInfo(
        string Alias,
        string DisplayName,
        Func<AppSettingsDto, bool> HasKey,
        string? KeyUrl,
        string KeyHint,
        bool KeyIsOptional = false);

    /// <summary>
    /// Every provider the picker offers, with where a key comes from.
    /// </summary>
    /// <remarks>
    /// KeyHint is shown beside the input so the user is not left guessing. The
    /// custom provider has no key page because its endpoint is whatever base URL
    /// the user typed, and its key is optional because only that endpoint can say
    /// whether it wants one.
    /// </remarks>
    internal static readonly IReadOnlyList<ProviderInfo> All =
    [
        new(
            "opencode",
            "OpenCode Zen",
            settings => settings.HasOpenCodeKey,
            "https://opencode.ai/zen",
            "Create a key in the OpenCode Zen dashboard."),
        new(
            "kilo",
            "Kilo Gateway",
            settings => settings.HasKiloKey,
            "https://kilocode.ai/",
            "Create a key in the Kilo dashboard."),
        new(
            "openai",
            "OpenAI",
            settings => settings.HasOpenAiKey,
            "https://platform.openai.com/api-keys",
            "Create a key under API keys in the OpenAI platform."),
        new(
            "anthropic",
            "Anthropic",
            settings => settings.HasAnthropicKey,
            "https://console.anthropic.com/settings/keys",
            "Create a key under API keys in the Anthropic console."),
        new(
            "custom",
            "Custom endpoint",
            settings => settings.HasCustomKey,
            null,
            "Paste the key this endpoint expects. Leave it empty if your base URL does not need one.",
            KeyIsOptional: true),
    ];

    /// <summary>
    /// Finds a provider by the alias the backend stores, or null when unknown.
    /// </summary>
    internal static ProviderInfo? Find(string? backendChoice)
    {
        string alias = Normalize(backendChoice);
        if (alias.Length == 0)
        {
            return null;
        }

        foreach (var provider in All)
        {
            if (string.Equals(provider.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// The display name for an alias, falling back to the raw alias so an
    /// unrecognised provider is still named rather than shown as blank.
    /// </summary>
    internal static string DisplayName(string? backendChoice)
    {
        var provider = Find(backendChoice);
        if (provider is not null)
        {
            return provider.DisplayName;
        }

        string alias = Normalize(backendChoice);
        return alias.Length == 0 ? "No provider" : alias;
    }

    /// <summary>
    /// True when the selected provider is missing the credential it needs.
    /// </summary>
    /// <param name="settings">
    /// The settings payload, or null when it could not be read. A null payload
    /// returns false: claiming a key is missing without having read the settings
    /// would send the user to a settings screen that may already be correct.
    /// </param>
    /// <remarks>
    /// Only a hosted provider is checked. A custom endpoint is whatever base URL the
    /// user pointed Ru at, and whether it wants a key is a property of that endpoint
    /// rather than something the app can know: a provider's API, a company gateway,
    /// and a server on this machine are all configured the same way and only differ
    /// in what they answer. Declaring a key missing there would be guessing, so the
    /// custom provider is left to the connection test, which asks the endpoint
    /// instead of assuming. This matches how the DeepSeek Harness models a provider:
    /// a base URL plus an optional key environment variable, with no hosted/local
    /// distinction and no key validation.
    /// </remarks>
    internal static bool IsMissingKey(AppSettingsDto? settings, string? backendChoice)
    {
        if (settings is null)
        {
            return false;
        }

        var provider = Find(backendChoice);
        if (provider is null || provider.KeyIsOptional)
        {
            return false;
        }

        return !provider.HasKey(settings);
    }

    /// <summary>
    /// The message shown when the selected provider has no key.
    /// </summary>
    internal static string MissingKeyMessage(AppSettingsDto? settings, string? backendChoice)
    {
        string name = DisplayName(backendChoice);
        if (settings is null)
        {
            return $"{name} has no API key configured. Open Settings to add one.";
        }

        var provider = Find(backendChoice);
        string hint = provider?.KeyHint ?? "Add the API key this provider expects.";
        return $"{name} has no API key configured. {hint}";
    }

    private static string Normalize(string? backendChoice) =>
        string.IsNullOrWhiteSpace(backendChoice)
            ? DefaultBackendChoice
            : backendChoice.Trim();
}
