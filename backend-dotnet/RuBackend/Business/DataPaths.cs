namespace RuBackend.Business;

public sealed record DataPaths(
    string MemoryDatabase,
    string LegacyMemoryTexts,
    string SettingsFile,
    string HistoryFile,
    string SessionDirectory,
    string ActivityLogFile,
    string LogRoot,
    string BootstrapLogRoot)
{
    public DataPaths(
        string memoryDatabase,
        string legacyMemoryTexts,
        string settingsFile,
        string historyFile,
        string sessionDirectory,
        string activityLogFile)
        : this(
            memoryDatabase,
            legacyMemoryTexts,
            settingsFile,
            historyFile,
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

        var memoryRoot = isolatedRoot
            ?? Path.Combine(resolvedProjectRoot, "data", "conversations");
        var businessRoot = isolatedRoot
            ?? Path.Combine(resolvedLocalAppData, "Ru");

        var resolvedLogRoot = !string.IsNullOrWhiteSpace(configuredLogRoot)
            ? Path.GetFullPath(configuredLogRoot.Trim())
            : Path.Combine(resolvedLocalAppData, "Ru", "logs");

        var bootstrapLogRoot = Path.Combine(resolvedLogRoot, "bootstrap");

        return new DataPaths(
            Path.Combine(memoryRoot, "memory.db"),
            Path.Combine(memoryRoot, "memory_texts.json"),
            Path.Combine(businessRoot, "settings.json"),
            Path.Combine(businessRoot, "history.json"),
            Path.Combine(businessRoot, "sessions"),
            Path.Combine(businessRoot, "activity_log.jsonl"),
            resolvedLogRoot,
            bootstrapLogRoot);
    }
}