namespace RuAgent.Hosting;

public enum RuntimeMigrationMode
{
    Dotnet,
    PythonCompatibility,
}

public static class RuntimeMigrationModeParser
{
    public static RuntimeMigrationMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RuntimeMigrationMode.Dotnet;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "dotnet" => RuntimeMigrationMode.Dotnet,
            "pythoncompatibility" or "python-compatibility" or "python_compatibility" => RuntimeMigrationMode.PythonCompatibility,
            _ => throw new ArgumentException("Unsupported runtime migration mode.", nameof(raw)),
        };
    }
}
