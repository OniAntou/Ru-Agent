namespace RuAgent.Hosting;

public enum RuntimeMigrationMode
{
    Dotnet,
}

public sealed class RuntimeMigrationModeSelectionException : ArgumentException
{
    public const string ErrorCode = "python_compatibility_retired";

    public RuntimeMigrationModeSelectionException()
        : base(ErrorCode)
    {
    }
}

public static class RuntimeMigrationModeParser
{
    public static RuntimeMigrationMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RuntimeMigrationMode.Dotnet;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is "pythoncompatibility" or "python-compatibility" or "python_compatibility")
        {
            throw new RuntimeMigrationModeSelectionException();
        }

        return normalized switch
        {
            "dotnet" => RuntimeMigrationMode.Dotnet,
            _ => throw new ArgumentException("Unsupported runtime migration mode.", nameof(raw)),
        };
    }
}
