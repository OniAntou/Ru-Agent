using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Providers;

internal static class AgentRunCallingModeCodec
{
    public static string ToStoredValue(ProviderToolCallingMode mode) => mode switch
    {
        ProviderToolCallingMode.Native => "native",
        ProviderToolCallingMode.TextSelection => "text_selection",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParseStoredValue(
        string? value,
        out ProviderToolCallingMode mode)
    {
        switch (value)
        {
            case "native":
                mode = ProviderToolCallingMode.Native;
                return true;
            case "text_selection":
                mode = ProviderToolCallingMode.TextSelection;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
