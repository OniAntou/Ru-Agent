namespace RuBackend.Business;

public sealed record DataPaths(
    string SettingsFile,
    string SessionDirectory,
    string ActivityLogFile,
    string LogRoot,
    string BootstrapLogRoot)
{
    public string BusinessDataRoot =>
        Path.GetDirectoryName(SettingsFile) ?? string.Empty;

    public string AgentHarnessDatabase =>
        Path.Combine(BusinessDataRoot, "agent_harness.db");

    public DataPaths(
        string settingsFile,
        string sessionDirectory,
        string activityLogFile)
        : this(
            settingsFile,
            sessionDirectory,
            activityLogFile,
            Path.Combine(Path.GetDirectoryName(settingsFile) ?? string.Empty, "logs"),
            Path.Combine(Path.GetDirectoryName(settingsFile) ?? string.Empty, "logs", "bootstrap"))
    {
    }

    public static bool TryEnsureDirectoryCreated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    public static DataPaths Resolve(
        string projectRoot,
        string userProfile,
        string localAppData,
        string? configuredDataRoot,
        string? configuredLogRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        var resolvedProjectRoot = Path.GetFullPath(projectRoot);
        var resolvedLocalAppData = Path.GetFullPath(localAppData);
        var isolatedRoot = string.IsNullOrWhiteSpace(configuredDataRoot)
            ? null
            : Path.GetFullPath(configuredDataRoot.Trim());

        var businessRoot = isolatedRoot
            ?? Path.Combine(resolvedLocalAppData, "Ru");

        var resolvedLogRoot = !string.IsNullOrWhiteSpace(configuredLogRoot)
            ? Path.GetFullPath(configuredLogRoot.Trim())
            : Path.Combine(resolvedLocalAppData, "Ru", "logs");

        var bootstrapLogRoot = Path.Combine(resolvedLogRoot, "bootstrap");

        return new DataPaths(
            Path.Combine(businessRoot, "settings.json"),
            Path.Combine(businessRoot, "sessions"),
            Path.Combine(businessRoot, "activity_log.jsonl"),
            resolvedLogRoot,
            bootstrapLogRoot);
    }
}
