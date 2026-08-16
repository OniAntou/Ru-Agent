using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class CredentialMigratorTests
{
    [Fact]
    public async Task MigrateAsync_moves_each_present_legacy_entry_and_removes_it_after_compare()
    {
        var store = new FakeCredentialStore();
        store.Values["mini_gpt/api_key"] = "opencode-secret";
        store.Values["mini_gpt/kilo_gateway_api_key"] = "kilo-secret";
        var migrator = new CredentialMigrator(store);

        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("opencode-secret", store.Values["Ru/api_key"]);
        Assert.Equal("kilo-secret", store.Values["Ru/kilo_gateway_api_key"]);
        Assert.False(store.Values.ContainsKey("mini_gpt/api_key"));
        Assert.False(store.Values.ContainsKey("mini_gpt/kilo_gateway_api_key"));
        Assert.Equal(2, result.MigratedAccounts.Count);
    }

    [Fact]
    public async Task MigrateAsync_leaves_absent_entries_untouched()
    {
        var store = new FakeCredentialStore();
        store.Values["Ru/api_key"] = "already-canonical";
        var migrator = new CredentialMigrator(store);

        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("already-canonical", store.Values["Ru/api_key"]);
        Assert.Empty(result.MigratedAccounts);
    }

    [Fact]
    public async Task MigrateAsync_fails_closed_when_canonical_read_does_not_compare_equal()
    {
        var store = new FakeCredentialStore { CorruptCanonicalRead = true };
        store.Values["mini_gpt/api_key"] = "secret";
        var migrator = new CredentialMigrator(store);

        var result = await migrator.MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("secret", store.Values["mini_gpt/api_key"]);
        Assert.True(store.Values.ContainsKey("Ru/api_key"));
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool CorruptCanonicalRead { get; init; }

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            var key = $"{serviceName}/{accountName}";
            if (CorruptCanonicalRead && serviceName == "Ru")
            {
                return Task.FromResult<string?>("corrupted-value");
            }

            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
        {
            Values[$"{serviceName}/{accountName}"] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            Values.Remove($"{serviceName}/{accountName}");
            return Task.CompletedTask;
        }
    }
}