using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class LegacyCredentialMigrationTests
{
    [Fact]
    public async Task Moves_python_and_pre_models_dev_accounts_to_the_current_names()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore();
        store.Values["mini_gpt/api_key"] = "python-opencode";
        store.Values["mini_gpt/kilo_gateway_api_key"] = "python-kilo";
        store.Values["mini_gpt/gemini_vision_api_key"] = "python-vision";
        var migration = temp.Migrator(store);

        var result = await migration.MigrateAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.AlreadyCompleted);
        Assert.Equal("python-opencode", store.Values["Ru/opencode_api_key"]);
        Assert.Equal("python-kilo", store.Values["Ru/kilo_api_key"]);
        Assert.Equal("python-vision", store.Values["Ru/gemini_vision_api_key"]);
        Assert.False(store.Values.ContainsKey("mini_gpt/api_key"));
        Assert.False(store.Values.ContainsKey("mini_gpt/kilo_gateway_api_key"));
        Assert.False(store.Values.ContainsKey("mini_gpt/gemini_vision_api_key"));
        Assert.Equal(3, result.MigratedAccounts.Count);
        Assert.True(File.Exists(temp.MarkerPath));
    }

    [Fact]
    public async Task The_last_net_account_wins_over_the_python_copy()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore();
        store.Values["Ru/api_key"] = "newer-net-opencode";
        store.Values["mini_gpt/api_key"] = "older-python-opencode";
        var migration = temp.Migrator(store);

        await migration.MigrateAsync(CancellationToken.None);

        Assert.Equal("newer-net-opencode", store.Values["Ru/opencode_api_key"]);
        Assert.False(store.Values.ContainsKey("mini_gpt/api_key"));
        Assert.False(store.Values.ContainsKey("Ru/api_key"));
    }

    [Fact]
    public async Task A_key_the_user_already_reentered_is_not_overwritten()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore();
        store.Values["Ru/opencode_api_key"] = "reentered";
        store.Values["mini_gpt/api_key"] = "stale";
        var migration = temp.Migrator(store);

        var result = await migration.MigrateAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.MigratedAccounts);
        Assert.Equal("reentered", store.Values["Ru/opencode_api_key"]);
        Assert.False(store.Values.ContainsKey("mini_gpt/api_key"));
    }

    [Fact]
    public async Task A_failed_read_back_keeps_the_source_and_does_not_record_completion()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore { CorruptWrites = true };
        store.Values["mini_gpt/api_key"] = "legacy";
        var migration = temp.Migrator(store);

        var result = await migration.MigrateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy", store.Values["mini_gpt/api_key"]);
        Assert.False(store.Values.ContainsKey("Ru/opencode_api_key"));
        Assert.False(File.Exists(temp.MarkerPath));
    }

    [Fact]
    public async Task A_failed_old_entry_delete_is_retried_on_the_next_start()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore { ThrowOnDelete = "mini_gpt/api_key" };
        store.Values["mini_gpt/api_key"] = "legacy";
        var migration = temp.Migrator(store);

        var first = await migration.MigrateAsync(CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal("legacy", store.Values["Ru/opencode_api_key"]);
        Assert.False(File.Exists(temp.MarkerPath));

        store.ThrowOnDelete = null;
        var second = await migration.MigrateAsync(CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.False(store.Values.ContainsKey("mini_gpt/api_key"));
        Assert.True(File.Exists(temp.MarkerPath));
    }

    [Fact]
    public async Task A_recorded_migration_never_runs_again()
    {
        using var temp = new TempMigration();
        var store = new FakeCredentialStore();
        store.Values["mini_gpt/api_key"] = "legacy";
        var migration = temp.Migrator(store);
        await migration.MigrateAsync(CancellationToken.None);

        store.Values["mini_gpt/api_key"] = "appeared-later";
        var second = await migration.MigrateAsync(CancellationToken.None);

        Assert.True(second.AlreadyCompleted);
        Assert.Empty(second.MigratedAccounts);
        Assert.Equal("appeared-later", store.Values["mini_gpt/api_key"]);
    }

    [Fact]
    public async Task A_plaintext_legacy_settings_key_is_imported_when_no_vault_key_exists()
    {
        using var temp = new TempMigration();
        var settingsPath = Path.Combine(temp.RootPath, "settings.json");
        File.WriteAllText(settingsPath,
            """{"backend":"OpenCode Zen","api_key":"plain-opencode","kilo_api_key":"plain-kilo"}""");
        var store = new FakeCredentialStore();
        var migration = temp.Migrator(store, legacySettingsFile: settingsPath);

        var result = await migration.MigrateAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("plain-opencode", store.Values["Ru/opencode_api_key"]);
        Assert.Equal("plain-kilo", store.Values["Ru/kilo_api_key"]);
        // The old file is evidence, not a thing this migration rewrites.
        Assert.Contains("plain-opencode", File.ReadAllText(settingsPath), StringComparison.Ordinal);
    }

    private sealed class TempMigration : IDisposable
    {
        public TempMigration()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "ru-legacy-credentials-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            MarkerPath = Path.Combine(RootPath, LegacyCredentialMigration.MarkerFileName);
        }

        public string RootPath { get; }

        public string MarkerPath { get; }

        public LegacyCredentialMigration Migrator(FakeCredentialStore store, string? legacySettingsFile = null) =>
            new(store, MarkerPath, legacySettingsFile, isWindows: true);

        public void Dispose()
        {
            try { Directory.Delete(RootPath, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool CorruptWrites { get; init; }

        public string? ThrowOnDelete { get; set; }

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(Key(serviceName, accountName)));

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
        {
            Values[Key(serviceName, accountName)] = CorruptWrites ? "corrupted" : secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            if (ThrowOnDelete == Key(serviceName, accountName))
            {
                throw new CredentialStoreException("delete refused");
            }

            Values.Remove(Key(serviceName, accountName));
            return Task.CompletedTask;
        }

        private static string Key(string serviceName, string accountName) => $"{serviceName}/{accountName}";
    }
}
