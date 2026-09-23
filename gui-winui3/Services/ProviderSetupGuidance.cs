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
    internal const string DefaultBackendChoice = "openai";

    /// <summary>Prefix for a Models.dev generic provider choice, e.g. "models.dev:openrouter".</summary>
    internal const string ModelsDevPrefix = "models.dev:";

    internal sealed record ProviderInfo(
        string Alias,
        string DisplayName,
        Func<AppSettingsDto, bool> HasKey,
        string KeyHint,
        bool KeyIsOptional = false);

    /// <summary>
    /// Every provider the picker offers, with the hint shown beside its key input.
    /// </summary>
    /// <remarks>
    /// The hint is plain text naming where a key is created. There is deliberately no
    /// URL: the user is already in the provider's own dashboard when they have one,
    /// and a link from settings added a field nothing else used.
    ///
    /// The custom provider's key is optional because only its endpoint can say
    /// whether one is wanted; see IsMissingKey.
    /// </remarks>
    internal static readonly IReadOnlyList<ProviderInfo> All =
    [
        new(
            "openai",
            "OpenAI",
            settings => settings.HasOpenAiKey,
            "Create a key under API keys in the OpenAI platform."),
        new(
            "anthropic",
            "Anthropic",
            settings => settings.HasAnthropicKey,
            "Create a key under API keys in the Anthropic console."),
        new(
            "custom",
            "Custom endpoint",
            settings => settings.HasCustomKey,
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
    /// Whether an alias names a provider the app knows.
    /// </summary>
    /// <remarks>
    /// The picker used to keep its own set of aliases beside this table, and a test
    /// compared the two to catch drift. A second copy plus a test that it still
    /// matches is a worse arrangement than having no second copy, so the question is
    /// asked of the table instead.
    ///
    /// Deliberately stricter than <see cref="Find"/>: Find answers "which provider
    /// should this be", so an empty alias becomes the default, while this answers
    /// "is this an alias I recognise", where empty is simply not one. Callers use
    /// this to reject input, and rejecting is the safe direction.
    /// </remarks>
    internal static bool IsKnown(string? backendChoice) =>
        !string.IsNullOrWhiteSpace(backendChoice) && Find(backendChoice) is not null;

    /// <summary>
    /// Whether the choice names a Models.dev generic provider ("models.dev:id").
    /// </summary>
    internal static bool IsModelsDevChoice(string? backendChoice)
    {
        if (string.IsNullOrWhiteSpace(backendChoice))
        {
            return false;
        }

        string trimmed = backendChoice.Trim();
        return trimmed.StartsWith(ModelsDevPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > ModelsDevPrefix.Length;
    }

    /// <summary>
    /// Whether the dropdown may select this choice: a fixed provider or a
    /// Models.dev generic provider. The fixed table cannot list the generic
    /// ones (they come from the network), so this is the union callers check.
    /// </summary>
    internal static bool IsSelectableProvider(string? backendChoice) =>
        IsKnown(backendChoice) || IsModelsDevChoice(backendChoice);

    /// <summary>
    /// Normalises a selectable choice for storage: the fixed alias as-is, a
    /// Models.dev id lowercased to match the backend's normalisation.
    /// Anything else falls back to the default.
    /// </summary>
    internal static string NormalizeProviderChoice(string? backendChoice)
    {
        if (IsModelsDevChoice(backendChoice))
        {
            string id = backendChoice!.Trim()[ModelsDevPrefix.Length..].Trim().ToLowerInvariant();
            return id.Length == 0 ? DefaultBackendChoice : ModelsDevPrefix + id;
        }

        return IsKnown(backendChoice) ? Normalize(backendChoice) : DefaultBackendChoice;
    }

    /// <summary>
    /// Sorts Models.dev dropdown rows by display name (A-Z, case-insensitive),
    /// falling back to the id when the name is blank. Duplicates collapse to
    /// the first occurrence so one provider never takes two rows.
    /// </summary>
    internal static IReadOnlyList<(string Id, string Name)> OrderModelsDevOptions(
        IEnumerable<(string Id, string Name)> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<(string Id, string Name)>();
        if (entries is null)
        {
            return cleaned;
        }

        foreach (var (rawId, rawName) in entries)
        {
            string id = (rawId ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(rawName) ? id : rawName.Trim();
            cleaned.Add((id, name));
        }

        cleaned.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) is { } order && order != 0
                ? order
                : string.Compare(left.Id, right.Id, StringComparison.Ordinal));
        return cleaned;
    }

    /// <summary>
    /// Whether a dropdown row survives the search filter: empty filter keeps
    /// everything, otherwise the name or id must contain it (case-insensitive).
    /// </summary>
    internal static bool MatchesProviderFilter(string? name, string? id, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string needle = filter.Trim();
        return (name ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (id ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase);
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
