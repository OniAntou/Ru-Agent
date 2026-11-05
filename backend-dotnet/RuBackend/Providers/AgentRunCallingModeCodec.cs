namespace RuBackend.Providers;

/// <summary>
/// The stored tool-calling mode of a run's provider pin. Native tool calling is the only
/// mode; a pin that names anything else (for example a run stored before the legacy
/// "text_selection" protocol was retired) cannot be resumed and fails closed with
/// <c>provider_selection_invalid</c> instead of being switched to native silently.
/// </summary>
internal static class AgentRunCallingModeCodec
{
    public const string Native = "native";

    public static void RequireNative(string? storedValue)
    {
        if (storedValue != Native)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
    }
}
