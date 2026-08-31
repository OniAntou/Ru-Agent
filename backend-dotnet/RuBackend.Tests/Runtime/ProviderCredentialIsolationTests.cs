using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Runtime;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class ProviderCredentialIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-provider-isolation-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Kilo Gateway", "kilo_gateway_api_key", "api.kilo.ai")]
    [InlineData("OpenCode Zen", "api_key", "opencode.ai")]
    [InlineData("Custom OpenAI", "custom_api_key", "fixture.invalid")]
    public async Task Missing_provider_key_never_reads_aliases_or_other_providers(string backend, string account, string host)
    {
        var credentials = new Credentials();
        credentials.Values.Remove(account);
        var data = await BootstrapAsync(backend, credentials);

        Assert.Empty(data.Credential);
        Assert.Equal(host, data.Endpoint.Host);
        Assert.Equal(new[] { account }, credentials.ReadAccounts);
    }

    [Theory]
    [InlineData("Kilo Gateway", "kilo_gateway_api_key")]
    [InlineData("OpenCode Zen", "api_key")]
    [InlineData("Custom OpenAI", "custom_api_key")]
    public async Task Configured_key_is_used_only_for_its_provider(string backend, string account)
    {
        var credentials = new Credentials();
        credentials.Values[account] = "fixture-selected-provider-key";
        var data = await BootstrapAsync(backend, credentials);

        Assert.Equal("fixture-selected-provider-key", data.Credential);
        Assert.Equal(new[] { account }, credentials.ReadAccounts);
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("LM Studio")]
    public async Task Local_provider_does_not_read_cloud_credentials(string backend)
    {
        var credentials = new Credentials();
        var data = await BootstrapAsync(backend, credentials);
        Assert.Empty(data.Credential);
        Assert.Empty(credentials.ReadAccounts);
    }

    private async Task<ProviderBootstrapData> BootstrapAsync(string backend, ICredentialStore credentials)
    {
        var paths = DataPaths.Resolve(_root, _root, _root, _root, Path.Combine(_root, "logs"));
        var settings = await new SettingsStore(paths).ReadAsync(CancellationToken.None);
        settings = settings with { Backend = backend, CustomUrl = "https://fixture.invalid/v1" };
        var path = await new ManagedProviderBootstrapFactory().CreateAsync(_root, settings, credentials, CancellationToken.None);
        return ProtectedProviderBootstrap.ReadAndDelete(_root, path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Credentials : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new()
        {
            ["api_key"] = "fixture-opencode-key", ["opencode_api_key"] = "fixture-opencode-alias",
            ["kilo_gateway_api_key"] = "fixture-kilo-key", ["kilo_api_key"] = "fixture-kilo-alias",
            ["custom_api_key"] = "fixture-custom-key",
        };
        public List<string> ReadAccounts { get; } = [];
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            Assert.Equal("Ru", serviceName);
            ReadAccounts.Add(accountName);
            return Task.FromResult(Values.GetValueOrDefault(accountName));
        }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
