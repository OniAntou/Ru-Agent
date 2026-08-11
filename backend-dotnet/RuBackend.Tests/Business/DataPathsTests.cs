using RuBackend.Business;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class DataPathsTests
{
    [Fact]
    public void Resolve_uses_project_memory_and_profile_business_defaults()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, configuredDataRoot: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectRoot, "data", "conversations", "memory.db")),
            paths.MemoryDatabase);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectRoot, "data", "conversations", "memory_texts.json")),
            paths.LegacyMemoryTexts);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userProfile, ".minigpt", "settings.json")),
            paths.SettingsFile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userProfile, ".minigpt", "history.json")),
            paths.HistoryFile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userProfile, ".minigpt", "sessions")),
            paths.SessionDirectory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userProfile, ".minigpt", "activity_log.jsonl")),
            paths.ActivityLogFile);
    }

    [Fact]
    public void Resolve_isolates_every_data_category_under_RU_DATA_ROOT()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-data", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, dataRoot);

        var expectedRoot = Path.GetFullPath(dataRoot);
        Assert.Equal(Path.Combine(expectedRoot, "memory.db"), paths.MemoryDatabase);
        Assert.Equal(Path.Combine(expectedRoot, "memory_texts.json"), paths.LegacyMemoryTexts);
        Assert.Equal(Path.Combine(expectedRoot, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(expectedRoot, "history.json"), paths.HistoryFile);
        Assert.Equal(Path.Combine(expectedRoot, "sessions"), paths.SessionDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "activity_log.jsonl"), paths.ActivityLogFile);
    }

    [Fact]
    public void Resolve_treats_whitespace_data_root_as_unconfigured()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, "   ");

        Assert.StartsWith(Path.GetFullPath(projectRoot), paths.MemoryDatabase, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(userProfile), paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
    }
}
