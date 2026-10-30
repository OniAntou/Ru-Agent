namespace RuAgent.Protocol.Providers;

/// <summary>
/// One named provider alias: the canonical backend the Host stores, the alias
/// clients use for key routes, and where its credential lives.
/// </summary>
/// <param name="Backend">Canonical backend value (<c>OpenAI</c>, <c>models.dev:opencode</c>, …).</param>
/// <param name="ClientAlias">Value a client sends for this provider (<c>openai</c>, <c>models.dev:kilo</c>).</param>
/// <param name="DisplayName">Name shown to the user when a command confirms a sign-in.</param>
/// <param name="KeyField">Settings field carrying the secret for a fixed provider; null for a Models.dev provider.</param>
/// <param name="KeyIsOptional">True for the keyless-capable Custom OpenAI endpoint.</param>
/// <param name="Spoken">Every name a user may type for this provider, including its canonical form.</param>
public sealed record ProviderAliasEntry(
    string Backend,
    string ClientAlias,
    string DisplayName,
    string? KeyField,
    bool KeyIsOptional,
    IReadOnlyList<string> Spoken)
{
    /// <summary>Whether this entry resolves through the Models.dev catalog.</summary>
    public bool IsModelsDev => Backend.StartsWith(ProviderAliases.ModelsDevPrefix, StringComparison.Ordinal);

    /// <summary>The catalog id for a Models.dev entry; null otherwise.</summary>
    public string? ModelsDevId => IsModelsDev ? Backend[ProviderAliases.ModelsDevPrefix.Length..] : null;
}

/// <summary>
/// The single source of provider naming shared by the Host and both clients.
/// </summary>
/// <remarks>
/// Three surfaces used to answer "which provider is this" with their own table:
/// the Host while resolving a run, the settings transition while normalizing a
/// backend choice, and the terminals/apps while routing a key. They drifted — the
/// desktop client once stripped the <c>models.dev:</c> prefix before posting, so
/// the Host rejected every catalog key as an invalid payload while the terminal
/// worked. One table makes that drift unrepresentable; a provider added here is
/// recognized everywhere at once.
/// </remarks>
public static class ProviderAliases
{
    public const string ModelsDevPrefix = "models.dev:";
    private const int MaximumModelsDevIdChars = 64;

    private static readonly ProviderAliasEntry[] Catalog =
    [
        new("OpenAI", "openai", "OpenAI", "openai_api_key", KeyIsOptional: false,
            ["openai", "openai api"]),
        new("Anthropic", "anthropic", "Anthropic", "anthropic_api_key", KeyIsOptional: false,
            ["anthropic", "anthropic claude", "claude"]),
        new("Custom OpenAI", "custom", "Custom OpenAI", "custom_api_key", KeyIsOptional: true,
            ["custom", "custom openai", "custom openai api"]),
        new(ModelsDevPrefix + "opencode", ModelsDevPrefix + "opencode", "opencode", KeyField: null,
            KeyIsOptional: false,
            ["opencode", "opencode zen", "opencode zen api", "opencode api", "gguf", "llama.cpp"]),
        new(ModelsDevPrefix + "kilo", ModelsDevPrefix + "kilo", "kilo", KeyField: null,
            KeyIsOptional: false,
            ["kilo", "kilo gateway", "kilo gateway api", "kilo api"]),
        new(ModelsDevPrefix + "openrouter", ModelsDevPrefix + "openrouter", "openrouter", KeyField: null,
            KeyIsOptional: false,
            ["openrouter"]),
    ];

    private static readonly Dictionary<string, ProviderAliasEntry> BySpoken = BuildIndex();

    /// <summary>Every named alias, in presentation order.</summary>
    public static IReadOnlyList<ProviderAliasEntry> Entries => Catalog;

    /// <summary>
    /// Canonical backend for a typed name, or null when nothing recognizes it.
    /// Accepts a Models.dev <c>models.dev:&lt;id&gt;</c> form, a spoken name
    /// (<c>claude</c>, <c>kilo</c>, <c>gguf</c>) and a canonical backend value.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (NormalizeModelsDev(value) is { } catalog)
        {
            return catalog;
        }

        return TryGet(value, out var entry) ? entry.Backend : null;
    }

    /// <summary>
    /// Canonical <c>models.dev:&lt;id&gt;</c> form, or null when the value is not a
    /// generic backend. The id is restricted to a bounded slug so it can safely
    /// derive credential account names.
    /// </summary>
    public static string? NormalizeModelsDev(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(ModelsDevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = trimmed[ModelsDevPrefix.Length..].Trim().ToLowerInvariant();
        if (id.Length == 0 || id.Length > MaximumModelsDevIdChars
            || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        return ModelsDevPrefix + id;
    }

    /// <summary>
    /// The named entry a value refers to, matched case-insensitively against its
    /// spoken names, canonical backend and client alias.
    /// </summary>
    public static bool TryGet(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProviderAliasEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return BySpoken.TryGetValue(value.Trim(), out entry);
    }

    private static Dictionary<string, ProviderAliasEntry> BuildIndex()
    {
        var index = new Dictionary<string, ProviderAliasEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Catalog)
        {
            foreach (var name in entry.Spoken
                         .Append(entry.Backend)
                         .Append(entry.ClientAlias)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!index.TryAdd(name.Trim(), entry))
                {
                    throw new InvalidOperationException(
                        $"Provider alias '{name}' is claimed by more than one provider.");
                }
            }
        }

        return index;
    }
}
