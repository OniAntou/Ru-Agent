namespace RuApp.Services;

internal sealed record ProviderKeyProbeResult(string Code, string? ErrorCode = null);

/// <summary>Publishes only the current edit's atomic Host validation/save result.</summary>
internal static class ProviderKeyProbe
{
    internal static async Task<ProviderKeyProbeResult> RunAsync(
        string providerId,
        string key,
        Func<bool> stillOwnsEdit,
        Func<string, string, Task<ProviderKeyProbeResult>> validateAndSave)
    {
        if (!stillOwnsEdit()) return new("superseded");
        var result = await validateAndSave(providerId, key);
        if (!stillOwnsEdit()) return new("superseded");
        return result;
    }
}
