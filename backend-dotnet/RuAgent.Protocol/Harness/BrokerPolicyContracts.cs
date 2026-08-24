namespace RuAgent.Protocol.Harness;

public static class BrokerPolicyContracts
{
    public const string StandardPresetWireValue = "standard";
    public const string FullAccessPresetWireValue = "full_access";

    public const string AllowWireValue = "allow";
    public const string AskWireValue = "ask";
    public const string DenyWireValue = "deny";

    public const string GlobalScopeWireValue = "global";
    public const string ProjectScopeWireValue = "project";
    public const string RunScopeWireValue = "run";
    public const string OnceScopeWireValue = "once";

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

    public static string CanonicalizeDecision(string decision) => decision switch
    {
        "allow" => AllowWireValue,
        "ask" => AskWireValue,
        "deny" => DenyWireValue,
        _ => throw new AgentProtocolException("invalid_decision"),
    };

    public static string CanonicalizeScope(string scope) => scope switch
    {
        "global" => GlobalScopeWireValue,
        "project" => ProjectScopeWireValue,
        "run" => RunScopeWireValue,
        "once" => OnceScopeWireValue,
        _ => throw new AgentProtocolException("invalid_scope"),
    };

    public static void ValidateSnapshotFields(long version, string presetWire, string sha256)
    {
        if (version <= 0)
        {
            throw new AgentProtocolException("invalid_snapshot_version");
        }

        _ = CanonicalizePreset(presetWire);

        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !IsLowerHex(sha256))
        {
            throw new AgentProtocolException("invalid_snapshot_hash");
        }
    }

    public static bool IsValidSha256(string value) =>
        value.Length == 64 && IsLowerHex(value);

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
