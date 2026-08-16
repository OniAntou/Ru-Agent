namespace RuBackend.Business;

public sealed record DataPaths(
    string MemoryDatabase,
    string LegacyMemoryTexts,
    string SettingsFile,
    string HistoryFile,
    string SessionDirectory,
    string ActivityLogFile)
{
    public static DataPaths Resolve(
        string projectRoot,
        string userProfile,
        string localAppData,
        string? configuredDataRoot)
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

        return new DataPaths(
            Path.Combine(memoryRoot, "memory.db"),
            Path.Combine(memoryRoot, "memory_texts.json"),
            Path.Combine(businessRoot, "settings.json"),
            Path.Combine(businessRoot, "history.json"),
            Path.Combine(businessRoot, "sessions"),
            Path.Combine(businessRoot, "activity_log.jsonl"));
    }
}