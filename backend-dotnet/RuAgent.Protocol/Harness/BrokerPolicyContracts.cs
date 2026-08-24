namespace RuAgent.Protocol.Harness;

public static class BrokerPolicyContracts
{
    public const string StandardPresetWireValue = "standard";
    public const string FullAccessPresetWireValue = "full_access";

    public static IReadOnlyList<string> AuthoritativeFallbackCapabilities()
    {
        return new[] { "files.list", "files.read" };
    }

    public static bool IsReadOnlyTool(string toolId) =>
        toolId is "files.list" or "files.read";

    public static string CanonicalizePreset(string preset) => preset switch
    {
        "standard" => StandardPresetWireValue,
        "full_access" => FullAccessPresetWireValue,
        _ => throw new AgentProtocolException("invalid_preset"),
    };
}
