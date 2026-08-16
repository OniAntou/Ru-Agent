using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;

namespace RuBackend.Business.Migration;

public static class MigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var projectRoot = Directory.GetCurrentDirectory();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        var legacyOverride = Environment.GetEnvironmentVariable("RU_LEGACY_DATA_ROOT");
        var isFakeLegacy = !string.IsNullOrWhiteSpace(legacyOverride);
        var legacyRoot = isFakeLegacy
            ? legacyOverride.Trim()
            : Path.Combine(userProfile, ".minigpt");
        var legacy = new DataPaths(
            Path.Combine(projectRoot, "data", "conversations", "memory.db"),
            Path.Combine(projectRoot, "data", "conversations", "memory_texts.json"),
            Path.Combine(legacyRoot, "settings.json"),
            Path.Combine(legacyRoot, "history.json"),
            Path.Combine(legacyRoot, "sessions"),
            Path.Combine(legacyRoot, "activity_log.jsonl"));
        var canonical = DataPaths.Resolve(projectRoot, userProfile, localAppData, dataRoot);

        Console.WriteLine($"[migrate] legacy_root={legacyRoot}");
        Console.WriteLine($"[migrate] canonical_root={Path.GetDirectoryName(canonical.SessionDirectory)}");

        var business = new BusinessDataMigrator(legacy, canonical);
        var businessResult = await business.MigrateAsync(CancellationToken.None);
        Console.WriteLine(
            $"[migrate] business_success={businessResult.Success} " +
            $"files={string.Join(',', businessResult.MigratedFiles)}");
        if (!businessResult.Success)
        {
            Console.Error.WriteLine($"[migrate] {businessResult.Message}");
            return 1;
        }

        var settings = await new SettingsStore(canonical).ReadAsync(CancellationToken.None);
        var sessions = await new SessionStore(canonical).ListAsync(CancellationToken.None);
        var history = await new HistoryStore(
            canonical,
            new SessionStore(canonical)).ReadLegacyAsync(CancellationToken.None);
        var activity = await new ActivityLogStore(canonical).ReadAsync(0, CancellationToken.None);
        Console.WriteLine(
            $"[migrate] verify settings_backend={settings.Backend} " +
            $"sessions={sessions.Count} history_messages={history.Count} activity_entries={activity.Count}");

        if (isFakeLegacy)
        {
            Console.WriteLine("[migrate] credentials_skipped=fake legacy override");
            return 0;
        }

        var credentials = new CredentialMigrator(new WindowsCredentialStore());
        var credentialResult = await credentials.MigrateAsync(CancellationToken.None);
        Console.WriteLine(
            $"[migrate] credentials_success={credentialResult.Success} " +
            $"migrated={string.Join(',', credentialResult.MigratedAccounts)}");
        return credentialResult.Success ? 0 : 1;
    }

    public static async Task<int> RestoreLegacyCredentialsAsync()
    {
        var store = new WindowsCredentialStore();
        var restored = new List<string>();
        foreach (var account in CredentialMigrator.MigratableAccounts)
        {
            var canonical = await store.GetAsync("Ru", account, CancellationToken.None)
                .ConfigureAwait(false);
            if (canonical is null)
            {
                continue;
            }

            await store.SetAsync("mini_gpt", account, canonical, CancellationToken.None)
                .ConfigureAwait(false);
            var legacy = await store.GetAsync("mini_gpt", account, CancellationToken.None)
                .ConfigureAwait(false);
            if (legacy is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(canonical),
                    System.Text.Encoding.UTF8.GetBytes(legacy)))
            {
                Console.Error.WriteLine($"[restore] failed for {account}; canonical entry preserved.");
                return 1;
            }

            await store.DeleteAsync("Ru", account, CancellationToken.None)
                .ConfigureAwait(false);
            restored.Add(account);
        }

        Console.WriteLine($"[restore] restored={string.Join(',', restored)}");
        return 0;
    }
}