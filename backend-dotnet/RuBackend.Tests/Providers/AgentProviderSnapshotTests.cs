using System.Net;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class AgentProviderSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-run-provider-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Same_run_keeps_model_key_and_effort_new_run_uses_updated_selection()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var initial = (await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider.invalid/v1" };
        await store.SaveAsync(ProviderReasoningPreferences.Save(initial, "high"), default);
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, _root, (_, _) => Task.FromResult(false));

        var first = await ReadAsync(factory, "run-a");
        Assert.Equal("a", first.Model);
        Assert.Equal("high", first.RequestOptions?["reasoning_effort"]?.GetValue<string>());
        Assert.Equal("key-one", first.Credential);

        credentials.Key = "key-two";
        await store.SaveAsync((await store.ReadAsync(default)) with { CustomModel = "b" }, default);
        var stillFirst = await ReadAsync(factory, "run-a");
        Assert.Equal("a", stillFirst.Model);
        Assert.Equal("key-one", stillFirst.Credential);
        Assert.Equal("high", stillFirst.RequestOptions?["reasoning_effort"]?.GetValue<string>());
        var next = await ReadAsync(factory, "run-b");
        Assert.Equal("b", next.Model);
        Assert.Equal("key-two", next.Credential);
        Assert.True(next.RequestOptions is null || next.RequestOptions.Count == 0);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Theory]
    [InlineData("missing", "default", "model_unavailable")]
    [InlineData("a", "max", "reasoning_unsupported")]
    public async Task Invalid_selection_never_writes_a_dispatch_handoff(string model, string effort, string expectedCode)
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var settings = (await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = model, CustomUrl = "https://provider.invalid/v1" };
        await store.SaveAsync(ProviderReasoningPreferences.Save(settings, effort), default);
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, _root, (_, _) => Task.FromResult(false));
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.CreateAsync(Request("run-a"), default));
        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    private async Task<ProviderBootstrapData> ReadAsync(AgentProviderSnapshotFactory factory, string run)
    {
        var path = await factory.CreateAsync(Request(run), default);
        return ProtectedProviderBootstrap.ReadAndDelete(_root, path);
    }

    private static ModelStepRequest Request(string run) => new(
        new RunId("run_" + new string(run.EndsWith('a') ? 'a' : 'b', 32)), new ModelStepId("step_" + Guid.NewGuid().ToString("N")),
        new ModelContextSnapshot([], ReadOnlyMemory<byte>.Empty), AgentPermissionPreset.Standard, 1, new string('a', 64));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Credentials : ICredentialStore
    {
        public string Key = "key-one";
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        { Assert.Equal("custom_api_key", accountName); return Task.FromResult<string?>(Key); }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                  {"data":[{"id":"a","supported_parameters":["reasoning_effort"],"reasoning_efforts":["low","high"]},{"id":"b"}]}
                  """),
            });
    }
}
