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
    /// <summary>Prefix for a Models.dev generic provider choice, e.g. "models.dev:openrouter".</summary>
    internal const string ModelsDevPrefix = "models.dev:";

    internal sealed record ProviderInfo(
        string Alias,
        string DisplayName,
        Func<AppSettingsDto, bool> HasKey,
        string KeyHint,
        bool KeyIsOptional = false);

    /// <summary>
    /// Every alias the app still knows how to name and check a key for, with the
    /// hint shown beside its key input.
    /// </summary>
    /// <remarks>
    /// The hint is plain text naming where a key is created. There is deliberately no
    /// URL: the user is already in the provider's own dashboard when they have one,
    /// and a link from settings added a field nothing else used.
    ///
    /// The custom provider's key is optional because only its endpoint can say
    /// whether one is wanted; see IsMissingKey.
    ///
    /// This table is not the picker. OpenAI and Anthropic remain here so a stored
    /// choice is still named and still reports a missing key, but neither is offered
    /// for new selection: provider choice is Models.dev's catalog plus the custom
    /// endpoint. See <see cref="PickerAliases"/>.
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
    /// The aliases the picker offers as fixed rows, in declaration order. Only the
    /// custom endpoint is fixed: it is the escape hatch for a server Models.dev cannot
    /// list, such as one on this machine. Everything else is selected from the catalog.
    /// </summary>
    internal static readonly IReadOnlyList<string> PickerAliases = ["custom"];

    /// <summary>
    /// Whether the alias is one the picker renders as a fixed row.
    /// </summary>
    internal static bool IsPickerFixed(string? backendChoice)
    {
        if (string.IsNullOrWhiteSpace(backendChoice))
        {
            return false;
        }

        string trimmed = backendChoice.Trim();
        return PickerAliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

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
            return id.Length == 0 ? UnsetChoice : ModelsDevPrefix + id;
        }

        return IsKnown(backendChoice) ? Normalize(backendChoice) : UnsetChoice;
    }

    /// <summary>
    /// The stored choice when the user has not picked one, or picked one the app no
    /// longer offers. It is never a provider alias: resolving it to a real provider
    /// needs the Models.dev catalog, which arrives after settings are read, so the
    /// caller that holds the catalog does the resolution. See
    /// <see cref="ResolveUnsetChoice"/>.
    /// </summary>
    internal const string UnsetChoice = "";

    /// <summary>
    /// Resolves an <see cref="UnsetChoice"/> to the first provider the catalog offers,
    /// A-Z. Returns the choice unchanged when it already names a provider, and
    /// <see cref="UnsetChoice"/> when the catalog is empty -- an unavailable list is
    /// not a reason to invent a provider the user never configured.
    /// </summary>
    internal static string ResolveUnsetChoice(
        string? backendChoice,
        IEnumerable<(string Id, string Name)>? catalog)
    {
        string choice = NormalizeProviderChoice(backendChoice);
        if (choice.Length > 0)
        {
            return choice;
        }

        if (catalog is null)
        {
            return UnsetChoice;
        }

        foreach (var (id, _) in OrderModelsDevOptions(catalog))
        {
            return ModelsDevPrefix + id;
        }

        return UnsetChoice;
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
    /// Signed-in dropdown rows, A-Z by display name: the fixed providers
    /// with a key plus the Models.dev entries with a stored key. The caller
    /// supplies the fixed rows' display names so this stays pure and the
    /// XAML remains the single home of the label text.
    /// </summary>
    internal static IReadOnlyList<(string Value, string Name)> OrderSignedInOptions(
        IEnumerable<(string Value, string Name, bool HasKey)> fixedProviders,
        IEnumerable<(string Id, string Name)> modelsDevEntries,
        IEnumerable<string>? modelsDevKeyIds)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (modelsDevKeyIds is not null)
        {
            foreach (var id in modelsDevKeyIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    keys.Add(id.Trim());
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signedIn = new List<(string Value, string Name)>();
        if (fixedProviders is not null)
        {
            foreach (var (value, name, hasKey) in fixedProviders)
            {
                if (hasKey && !string.IsNullOrWhiteSpace(value) && seen.Add(value.Trim()))
                {
                    signedIn.Add((value.Trim(), string.IsNullOrWhiteSpace(name) ? value.Trim() : name.Trim()));
                }
            }
        }

        if (modelsDevEntries is not null)
        {
            foreach (var (rawId, rawName) in modelsDevEntries)
            {
                string id = (rawId ?? string.Empty).Trim().ToLowerInvariant();
                if (id.Length == 0 || !keys.Contains(id) || !seen.Add(ModelsDevPrefix + id))
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(rawName) ? id : rawName.Trim();
                signedIn.Add((ModelsDevPrefix + id, name));
            }
        }

        signedIn.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) is { } order && order != 0
                ? order
                : string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));
        return signedIn;
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
    /// <summary>
    /// The stored choice the editor should show. A fixed alias the picker no longer
    /// offers (OpenAI, Anthropic) with no key stored is not a working choice: it has no
    /// picker row and no key row, so a new profile, whose Host default is OpenAI, left
    /// the user nowhere to type a key. Such a choice reads as unset and resolves against
    /// the catalog like any first run. A stored key keeps the choice, so an existing
    /// setup keeps working.
    /// </summary>
    internal static string EffectiveStoredChoice(string? backendChoice, AppSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string choice = backendChoice?.Trim() ?? string.Empty;
        if (!IsKnown(choice) || IsPickerFixed(choice))
        {
            return choice;
        }

        var provider = Find(choice);
        return provider is not null && !provider.KeyIsOptional && !provider.HasKey(settings)
            ? UnsetChoice
            : choice;
    }

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
            ? UnsetChoice
            : backendChoice.Trim();
}
