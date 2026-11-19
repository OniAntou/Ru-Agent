using System.Text.Json.Nodes;
using RuAgent.Protocol.Providers;
using RuBackend.Providers;

namespace RuBackend.Business.Settings;

/// <summary>
/// What the Host last learned about each stored provider key, kept in the non-secret
/// settings under <c>provider_key_status</c> and returned by <c>GET /settings</c>.
/// </summary>
/// <remarks>
/// Keyed by canonical backend (<c>OpenAI</c>, <c>models.dev:openrouter</c>,
/// <c>custom:lab</c>, <c>Custom OpenAI</c>). Each entry is a state and when it was
/// learned, never anything derived from the key. Only the Host writes it: a settings
/// payload cannot set it. An absent entry means nothing is known yet, as for keys
/// stored before this existed.
/// </remarks>
public static class ProviderKeyStatus
{
    public const string SettingField = "provider_key_status";

    /// <summary>The provider accepted the key on a request that needs one.</summary>
    public const string Verified = "verified";

    /// <summary>Stored, but nothing has yet told a good key from a bad one.</summary>
    public const string Unchecked = "unchecked";

    /// <summary>The provider answered HTTP 401 to this key during a run.</summary>
    public const string Rejected = "rejected";

    private const int MaximumEntries = 256;

    public static string? Read(SettingsSnapshot settings, string backend) =>
        settings.Raw[SettingField] is JsonObject map
        && map[backend] is JsonObject entry
        && entry["state"] is JsonValue value
        && value.TryGetValue<string>(out var state)
            ? state
            : null;

    /// <summary>Records a state in <paramref name="raw"/>; older entries go first when full.</summary>
    public static void Set(JsonObject raw, string backend, string state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw[SettingField] is not JsonObject map)
        {
            map = new JsonObject();
            raw[SettingField] = map;
        }

        map.Remove(backend);
        while (map.Count >= MaximumEntries)
            map.Remove(map.First().Key);
        map[backend] = new JsonObject
        {
            ["state"] = state,
            ["checked_at"] = at.ToUniversalTime().ToString("O"),
        };
    }

    public static void Remove(JsonObject raw, string backend)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw[SettingField] is JsonObject map) map.Remove(backend);
    }

    /// <summary>
    /// The canonical backend a credential account belongs to, or null for an account no
    /// provider reads. The inverse of <see cref="ProviderSelection.CredentialAccountForAlias"/>.
    /// </summary>
    public static string? BackendForAccount(string account) => account switch
    {
        "custom_api_key" => "Custom OpenAI",
        "openai_api_key" => "OpenAI",
        "anthropic_api_key" => "Anthropic",
        _ when account.StartsWith("custom_provider_", StringComparison.Ordinal)
            && account.EndsWith("_key", StringComparison.Ordinal)
            && account["custom_provider_".Length..^"_key".Length] is var id
            && CustomProviders.IsValidId(id) => CustomProviders.BackendPrefix + id,
        _ when account.EndsWith("_api_key", StringComparison.Ordinal) =>
            ProviderAliases.NormalizeModelsDev(ProviderAliases.ModelsDevPrefix + account[..^"_api_key".Length]),
        _ => null,
    };

    /// <summary>
    /// Drops the entries whose endpoint this save moves: a key checked against one
    /// server says nothing about another.
    /// </summary>
    public static void ForgetMovedEndpoints(SettingsSnapshot previous, SettingsSnapshot candidate, JsonObject raw)
    {
        if (!string.Equals(previous.CustomUrl, candidate.CustomUrl, StringComparison.Ordinal))
            Remove(raw, "Custom OpenAI");
        if (!string.Equals(previous.CloudflareAccountId, candidate.CloudflareAccountId, StringComparison.Ordinal))
            Remove(raw, ProviderAliases.ModelsDevPrefix + ProviderOverrides.CloudflareId);
        var after = CustomProviders.Read(candidate).ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        foreach (var provider in CustomProviders.Read(previous))
        {
            if (!after.TryGetValue(provider.Id, out var now)
                || now.BaseUrl != provider.BaseUrl
                || now.Protocol != provider.Protocol)
            {
                Remove(raw, CustomProviders.BackendPrefix + provider.Id);
            }
        }
    }
}
