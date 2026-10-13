using System.Text.Json.Nodes;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class SettingsTransitionRemoveProviderTests
{
    [Theory]
    [InlineData("models.dev:openrouter")]
    [InlineData("MODELS.DEV:OpenRouter")]
    public async Task Models_dev_provider_can_be_signed_out_offline_without_touching_other_keys(string providerChoice)
    {
        var settings = new MemorySettingsStore(new SettingsSnapshot(
            new JsonObject(), "models.dev:openrouter", "vendor/model", "default",
            "http://localhost:8000/v1", "", ""));
        var credentials = new MemoryCredentialStore(
            ("openrouter_api_key", "openrouter-secret"),
            ("anthropic_api_key", "anthropic-secret"));
        var service = new SettingsTransitionService(settings, credentials);

        var first = await service.RemoveProviderAsync(providerChoice, CancellationToken.None);
        var second = await service.RemoveProviderAsync(providerChoice, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal("Provider signed out.", first.Message);
        Assert.True(second.Success);
        Assert.Equal("Provider is already signed out.", second.Message);
        Assert.Null(credentials.Value("openrouter_api_key"));
        Assert.Equal("anthropic-secret", credentials.Value("anthropic_api_key"));
        Assert.Equal(new[] { "openrouter_api_key" }, credentials.DeletedAccounts);
    }

    [Theory]
    [InlineData("models.dev:../openrouter")]
    [InlineData("openrouter")]
    public async Task Invalid_or_unknown_models_dev_alias_is_rejected_without_deleting_credentials(string providerChoice)
    {
        var settings = new MemorySettingsStore(new SettingsSnapshot(
            new JsonObject(), "models.dev:openrouter", "vendor/model", "default",
            "http://localhost:8000/v1", "", ""));
        var credentials = new MemoryCredentialStore(("openrouter_api_key", "openrouter-secret"));
        var service = new SettingsTransitionService(settings, credentials);

        var result = await service.RemoveProviderAsync(providerChoice, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Unknown provider.", result.Message);
        Assert.Equal("openrouter-secret", credentials.Value("openrouter_api_key"));
        Assert.Empty(credentials.DeletedAccounts);
    }

    private sealed class MemorySettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryCredentialStore(params (string Account, string Secret)[] entries) : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = entries.ToDictionary(item => item.Account, item => item.Secret, StringComparer.Ordinal);
        public List<string> DeletedAccounts { get; } = [];
        public string? Value(string account) => _values.GetValueOrDefault(account);

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault(accountName));

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
        {
            _values[accountName] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            _values.Remove(accountName);
            DeletedAccounts.Add(accountName);
            return Task.CompletedTask;
        }
    }
}
