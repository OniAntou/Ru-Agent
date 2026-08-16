using RuBackend.Business;
using RuBackend.Business.Migration;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class BusinessDataMigratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-migrate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateLegacyDirectory()
    {
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "sessions"));
        return legacy;
    }

    private static DataPaths LegacyPaths(string legacyRoot) => new(
        Path.Combine(legacyRoot, "memory.db"),
        Path.Combine(legacyRoot, "memory_texts.json"),
        Path.Combine(legacyRoot, "settings.json"),
        Path.Combine(legacyRoot, "history.json"),
        Path.Combine(legacyRoot, "sessions"),
        Path.Combine(legacyRoot, "activity_log.jsonl"));

    private static DataPaths CanonicalPaths(string canonicalRoot) => new(
        Path.Combine(canonicalRoot, "memory.db"),
        Path.Combine(canonicalRoot, "memory_texts.json"),
        Path.Combine(canonicalRoot, "settings.json"),
        Path.Combine(canonicalRoot, "history.json"),
        Path.Combine(canonicalRoot, "sessions"),
        Path.Combine(canonicalRoot, "activity_log.jsonl"));

    [Fact]
    public async Task MigrateAsync_stages_only_approved_files_and_activates_atomically()
    {
        var legacy = CreateLegacyDirectory();
        var canonical = Path.Combine(_root, "canonical");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"voice_style_id\":\"original\"}", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "history.json"), "[]", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "activity_log.jsonl"), "{}\n", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "pyvts_token.txt"), "token-value", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "sessions", "abc.json"), "{\"messages\":[]}", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "unexpected.bin"), "not-approved", System.Text.Encoding.UTF8);

        var migrator = new BusinessDataMigrator(LegacyPaths(legacy), CanonicalPaths(canonical));
        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(canonical, "settings.json")));
        Assert.True(File.Exists(Path.Combine(canonical, "history.json")));
        Assert.True(File.Exists(Path.Combine(canonical, "activity_log.jsonl")));
        Assert.True(File.Exists(Path.Combine(canonical, "pyvts_token.txt")));
        Assert.True(File.Exists(Path.Combine(canonical, "sessions", "abc.json")));
        Assert.False(File.Exists(Path.Combine(canonical, "unexpected.bin")));
        Assert.False(Directory.EnumerateFiles(canonical, "*", SearchOption.AllDirectories)
            .Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MigrateAsync_rejects_links_and_unexpected_nesting()
    {
        var legacy = CreateLegacyDirectory();
        var canonical = Path.Combine(_root, "canonical");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}", System.Text.Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(legacy, "sessions", "nested"));
        File.WriteAllText(Path.Combine(legacy, "sessions", "nested", "x.json"), "{}", System.Text.Encoding.UTF8);

        var migrator = new BusinessDataMigrator(LegacyPaths(legacy), CanonicalPaths(canonical));
        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("nested", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(canonical));
    }

    [Fact]
    public async Task MigrateAsync_validates_json_and_sha256_before_activation()
    {
        var legacy = CreateLegacyDirectory();
        var canonical = Path.Combine(_root, "canonical");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{invalid", System.Text.Encoding.UTF8);

        var migrator = new BusinessDataMigrator(LegacyPaths(legacy), CanonicalPaths(canonical));
        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(canonical));
    }

    [Fact]
    public async Task MigrateAsync_never_overwrites_non_empty_canonical_data()
    {
        var legacy = CreateLegacyDirectory();
        var canonical = Path.Combine(_root, "canonical");
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "existing.json"), "keep", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}", System.Text.Encoding.UTF8);

        var migrator = new BusinessDataMigrator(LegacyPaths(legacy), CanonicalPaths(canonical));
        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(canonical, "existing.json")));
    }

    [Fact]
    public async Task MigrateAsync_treats_existing_empty_canonical_directory_as_replaceable()
    {
        var legacy = CreateLegacyDirectory();
        var canonical = Path.Combine(_root, "canonical");
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}", System.Text.Encoding.UTF8);

        var migrator = new BusinessDataMigrator(LegacyPaths(legacy), CanonicalPaths(canonical));
        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(canonical, "settings.json")));
    }
}