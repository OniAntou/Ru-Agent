using RuAgent.Protocol.Providers;

namespace RuAgent.Client;

/// <summary>What a provider alias means for a credential: how it is named and where it goes.</summary>
/// <remarks>
/// <paramref name="Choice"/> is the canonical form the backend accepts; the settings
/// payload carries this, never a bare Models.dev id. <paramref name="KeyField"/> is the
/// settings field that carries the secret for a fixed provider, and null for a
/// Models.dev provider, whose key travels as the generic
/// <c>models_dev_provider_id</c> + <c>models_dev_api_key</c> pair instead.
/// </remarks>
public sealed record ProviderCredentialTarget(
    string Choice,
    string DisplayName,
    string? KeyField,
    string? ModelsDevId,
    bool KeyIsOptional = false)
{
    public bool IsModelsDev => ModelsDevId is not null;
}

/// <summary>
/// The one place that answers "which provider is this, and where does its key go".
/// </summary>
/// <remarks>
/// The terminal client and the desktop app both collect API keys and both post them to
/// the same settings endpoint, and they each grew their own copy of this mapping. They
/// then disagreed: the desktop app stripped the <c>models.dev:</c> prefix before sending,
/// so the backend rejected every catalog key as an invalid payload, while the terminal
/// sent the canonical form and worked. One table, used by both, is what stops that
/// happening again.
/// </remarks>
public static class ProviderCredential
{
    public const string ModelsDevPrefix = ProviderAliases.ModelsDevPrefix;

    /// <summary>
    /// Resolves what a user typed, or null when nothing recognises it. Accepts a fixed
    /// alias, a spoken variant, or the canonical <c>models.dev:id</c> form. The names
    /// themselves live in <see cref="ProviderAliases"/> so this surface cannot disagree
    /// with the Host about what a provider is called.
    /// </summary>
    public static ProviderCredentialTarget? Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string trimmed = input.Trim();
        if (trimmed.StartsWith(ModelsDevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return FromModelsDevId(trimmed[ModelsDevPrefix.Length..]);
        }

        if (!ProviderAliases.TryGet(trimmed, out var entry))
        {
            return null;
        }

        return new ProviderCredentialTarget(
            entry.ClientAlias,
            entry.DisplayName,
            entry.KeyField,
            entry.ModelsDevId,
            entry.KeyIsOptional);
    }

    /// <summary>
    /// Whether a stored or selected value names a catalog provider. The picker cannot
    /// offer a malformed one, but a stored choice can outlive the rules that wrote it.
    /// </summary>
    public static bool IsModelsDevChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return false;
        }

        string trimmed = choice.Trim();
        return trimmed.StartsWith(ModelsDevPrefix, StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > ModelsDevPrefix.Length;
    }

    /// <summary>
    /// The canonical form of a catalog choice: prefix lowercased, id lowercased. Returns
    /// null when the value does not name a catalog provider.
    /// </summary>
    public static string? CanonicalModelsDevChoice(string? choice) =>
        IsModelsDevChoice(choice)
            ? ModelsDevPrefix + choice!.Trim()[ModelsDevPrefix.Length..].Trim().ToLowerInvariant()
            : null;

    /// <summary>
    /// The settings payload that stores a key. A fixed provider sends its own field; a
    /// catalog provider sends the generic pair, whose id must keep the
    /// <c>models.dev:</c> prefix because the backend normalizes the field into a backend
    /// choice and rejects a bare id as an invalid payload.
    /// </summary>
    public static Dictionary<string, string> KeyPayload(ProviderCredentialTarget target, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(apiKey);

        return target.IsModelsDev
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["models_dev_provider_id"] = target.Choice,
                ["models_dev_api_key"] = apiKey,
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [target.KeyField!] = apiKey,
            };
    }

    private static ProviderCredentialTarget? FromModelsDevId(string id)
    {
        string normalized = id.Trim().ToLowerInvariant();
        // The id derives credential account names on the host, so it is restricted to a
        // bounded slug. Anything else is not a provider, and guessing one would write a
        // key to an account nothing reads.
        if (normalized.Length == 0 || normalized.Length > 64
            || normalized.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        return new ProviderCredentialTarget(ModelsDevPrefix + normalized, normalized, null, normalized);
    }
}
