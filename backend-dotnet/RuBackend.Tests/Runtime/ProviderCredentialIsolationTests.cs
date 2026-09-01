using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Runtime;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class ProviderCredentialIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-provider-isolation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Startup_handoff_is_neutral_and_contains_no_provider_credential()
    {
        var path = await new ManagedProviderBootstrapFactory().CreateAsync(
            _root, CancellationToken.None);
        var data = ProtectedProviderBootstrap.ReadAndDelete(_root, path);

        Assert.Empty(data.Credential);
        Assert.Equal("per-step", data.Backend);
        Assert.Equal("per-step-required", data.Model);
        Assert.Equal("127.0.0.1", data.Endpoint.Host);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
