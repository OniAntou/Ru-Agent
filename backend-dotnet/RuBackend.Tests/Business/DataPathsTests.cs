using RuBackend.Business;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class DataPathsTests
{
    [Fact]
    public void Resolve_uses_project_memory_and_localappdata_ru_business_defaults()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "ru-local", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, localAppData, configuredDataRoot: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectRoot, "data", "conversations", "memory.db")),
            paths.MemoryDatabase);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectRoot, "data", "conversations", "memory_texts.json")),
            paths.LegacyMemoryTexts);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "settings.json")),
            paths.SettingsFile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "history.json")),
            paths.HistoryFile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "sessions")),
            paths.SessionDirectory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "activity_log.jsonl")),
            paths.ActivityLogFile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "logs")),
            paths.LogRoot);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localAppData, "Ru", "logs", "bootstrap")),
            paths.BootstrapLogRoot);
    }

    [Fact]
    public void Resolve_isolates_every_data_category_under_RU_DATA_ROOT()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "ru-local", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(Path.GetTempPath(), "ru-data", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, localAppData, dataRoot);

        var expectedRoot = Path.GetFullPath(dataRoot);
        Assert.Equal(Path.Combine(expectedRoot, "memory.db"), paths.MemoryDatabase);
        Assert.Equal(Path.Combine(expectedRoot, "memory_texts.json"), paths.LegacyMemoryTexts);
        Assert.Equal(Path.Combine(expectedRoot, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(expectedRoot, "history.json"), paths.HistoryFile);
        Assert.Equal(Path.Combine(expectedRoot, "sessions"), paths.SessionDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "activity_log.jsonl"), paths.ActivityLogFile);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "Ru", "logs")), paths.LogRoot);
    }

    [Fact]
    public void Resolve_uses_explicit_configured_log_root_and_derives_bootstrap()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "ru-local", Guid.NewGuid().ToString("N"));
        var logRoot = Path.Combine(Path.GetTempPath(), "ru-custom-logs", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, localAppData, configuredDataRoot: null, configuredLogRoot: logRoot);

        var expectedLogRoot = Path.GetFullPath(logRoot);
        Assert.Equal(expectedLogRoot, paths.LogRoot);
        Assert.Equal(Path.Combine(expectedLogRoot, "bootstrap"), paths.BootstrapLogRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "Ru", "activity_log.jsonl")), paths.ActivityLogFile);
    }

    [Fact]
    public void Resolve_treats_whitespace_data_and_log_roots_as_unconfigured()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "ru-project", Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(Path.GetTempPath(), "ru-profile", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "ru-local", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.Resolve(projectRoot, userProfile, localAppData, "   ", "   ");

        Assert.StartsWith(Path.GetFullPath(projectRoot), paths.MemoryDatabase, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(localAppData, "Ru")), paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "Ru", "logs")), paths.LogRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "Ru", "logs", "bootstrap")), paths.BootstrapLogRoot);
    }

    [Fact]
    public void TryEnsureDirectoryCreated_handles_null_or_invalid_paths_safely()
    {
        Assert.False(DataPaths.TryEnsureDirectoryCreated(null));
        Assert.False(DataPaths.TryEnsureDirectoryCreated(""));
        Assert.False(DataPaths.TryEnsureDirectoryCreated("   "));
        Assert.False(DataPaths.TryEnsureDirectoryCreated("Z:\\nonexistent_drive_12345\\impossible_path"));
    }
}