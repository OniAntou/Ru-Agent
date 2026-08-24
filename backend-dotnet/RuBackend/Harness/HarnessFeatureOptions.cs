namespace RuBackend.Harness;

public sealed class HarnessFeatureOptions
{
    public const string SectionName = "RuBackend:AgentHarness";
    public const string VersionV1 = "v1";
    public const string VersionV2 = "v2";
    public const string DefaultVersion = VersionV1;

    public string Version { get; }

    public HarnessFeatureOptions(string? version)
    {
        Version = Parse(version);
    }

    public static string Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultVersion;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            VersionV1 => VersionV1,
            VersionV2 => VersionV2,
            _ => throw new ArgumentException($"Harness version must be '{VersionV1}' or '{VersionV2}'.", nameof(value)),
        };
    }

    public bool IsV2 => Version == VersionV2;
}
