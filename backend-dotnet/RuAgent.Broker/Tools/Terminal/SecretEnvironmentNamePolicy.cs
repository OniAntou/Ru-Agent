namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Case-insensitive secret-name detection for environment entries. Names that
/// carry a bearer/credential meaning or belong to a Ru-owned component are
/// always stripped from the child command environment.
/// </summary>
public static class SecretEnvironmentNamePolicy
{
    private static readonly string[] SecretSubstrings =
    [
        "token",
        "secret",
        "password",
        "passwd",
        "apikey",
        "providerkey",
        "credential",
        "connectionstring",
        "privatekey",
        "accesskey",
        "refreshkey",
        "authkey",
    ];

    public static bool IsSecretName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var normalized = name.ToLowerInvariant();
        var compact = normalized.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        return IsRuOwnedName(normalized)
               || SecretSubstrings.Any(substring => compact.Contains(substring, StringComparison.Ordinal));
    }

    private static bool IsRuOwnedName(string normalized)
    {
        return normalized.StartsWith("ru_", StringComparison.Ordinal)
               || normalized.Contains("broker", StringComparison.Ordinal)
               || normalized.Contains("worker", StringComparison.Ordinal)
               || normalized.Contains("desktop", StringComparison.Ordinal)
               || normalized.Contains("ruapp", StringComparison.Ordinal);
    }
}
