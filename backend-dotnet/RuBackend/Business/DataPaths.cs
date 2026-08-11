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
        string? configuredDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);

        var resolvedProjectRoot = Path.GetFullPath(projectRoot);
        var resolvedUserProfile = Path.GetFullPath(userProfile);
        var isolatedRoot = string.IsNullOrWhiteSpace(configuredDataRoot)
            ? null
            : Path.GetFullPath(configuredDataRoot.Trim());

        var memoryRoot = isolatedRoot
            ?? Path.Combine(resolvedProjectRoot, "data", "conversations");
        var businessRoot = isolatedRoot
            ?? Path.Combine(resolvedUserProfile, ".minigpt");

        return new DataPaths(
            Path.Combine(memoryRoot, "memory.db"),
            Path.Combine(memoryRoot, "memory_texts.json"),
            Path.Combine(businessRoot, "settings.json"),
            Path.Combine(businessRoot, "history.json"),
            Path.Combine(businessRoot, "sessions"),
            Path.Combine(businessRoot, "activity_log.jsonl"));
    }
}
