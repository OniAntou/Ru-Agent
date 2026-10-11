using RuApp.Models;

namespace RuApp.Services;

internal sealed record ProviderKeyProbeResult(string Code, string? ErrorCode = null);

/// <summary>Validates the saved key against its own provider, with ownership checks at every await.</summary>
internal static class ProviderKeyProbe
{
    internal static async Task<ProviderKeyProbeResult> RunAsync(
        string providerId,
        string key,
        Func<bool> stillOwnsEdit,
        Func<string, string, Task<bool>> save,
        Func<string, Task<ProviderCatalogSnapshot?>> fetch,
        Func<string, Task<bool>> remove)
    {
        var saved = await save(providerId, key);
        if (!stillOwnsEdit()) return new("superseded");
        if (!saved) return new("save_failed");
        var snapshot = await fetch(providerId);
        if (!stillOwnsEdit()) return new("superseded");
        if (snapshot is null || !string.Equals(snapshot.Backend, providerId, StringComparison.OrdinalIgnoreCase))
            return new("unverified", "catalog_unavailable");
        if (snapshot.IsSuccess) return new("valid");
        if (!string.Equals(snapshot.ErrorCode, "catalog_unauthorized", StringComparison.OrdinalIgnoreCase))
            return new("unverified", snapshot.ErrorCode);
        var removed = await remove(providerId);
        if (!stillOwnsEdit()) return new("superseded");
        return removed ? new("invalid") : new("remove_failed");
    }
}
