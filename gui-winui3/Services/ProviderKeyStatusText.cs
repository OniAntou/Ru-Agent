using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The provider overview's wording for a stored key, from what the Host last learned
/// about it (<c>provider_key_status</c>). Only called for a provider with a key stored.
/// </summary>
internal static class ProviderKeyStatusText
{
    internal const string Unknown = "Key saved";
    internal const string Verified = "Key saved · working";
    internal const string Unchecked = "Key saved · not checked yet";
    internal const string Rejected = "Key refused by the provider · replace it";

    internal static string Saved(AppSettingsDto? settings, string backend) =>
        settings?.ProviderKeyStatus is { } map && map.TryGetValue(backend, out var entry)
            ? entry.State switch
            {
                "verified" => Verified,
                "unchecked" => Unchecked,
                "rejected" => Rejected,
                _ => Unknown,
            }
            : Unknown;
}
