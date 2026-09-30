using System.Net;
using RuAgent.Harness.Core;
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
        await store.SaveAsync(ProviderReasoningPreferences.Save(initial, "high", ProviderSelection.From(initial)), default);
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, (_, _) => Task.FromResult(false));

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
        await store.SaveAsync(ProviderReasoningPreferences.Save(settings, effort, ProviderSelection.From(settings)), default);
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, (_, _) => Task.FromResult(false));
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(RunId("run-a"), default));
        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Fact]
    public async Task Missing_key_fails_fast_before_any_network_fetch()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "OpenAI", OpenAiModel = "gpt-4o" }, default);
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), new EmptyCredentials(), TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, new EmptyCredentials(), catalog, (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(RunId("run-a"), default));
        Assert.Equal("catalog_unauthorized", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Fact]
    public async Task Missing_model_fails_fast_before_any_network_fetch()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "OpenAI", OpenAiModel = "" }, default);
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), new KeyedCredentials(), TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(store, new KeyedCredentials(), catalog, (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(RunId("run-a"), default));
        Assert.Equal("model_unavailable", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>
    /// A catalog that cannot be read is reported as itself, never as a missing model.
    /// </summary>
    /// <remarks>
    /// The model check reads the catalog, so the two failures sit on one path and it
    /// would be easy to collapse them. They need different codes because they need
    /// different fixes: a rejected key, a rate limit and a server error are not solved by
    /// changing the model, and a model that really is absent is not solved by retrying.
    /// This pins the separation rather than a past defect.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "catalog_unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "catalog_rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "catalog_http_error")]
    public async Task An_unreadable_catalog_reports_its_own_failure_not_a_missing_model(
        HttpStatusCode status, string expectedCode)
    {
        // The catalog endpoint comes from the models.dev entry, so the handler can
        // answer for it. A key is present, so the credential check does not fire first
        // and the failure genuinely originates in the catalog read.
        var cacheFile = await WriteModelsDevCacheAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash:free" }, default);
        var credentials = new KeyedCredentials();
        var catalog = new ProviderCatalogService(
            new HttpClient(new FailingCatalogHandler(status)), credentials, TimeProvider.System, cacheFile);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(RunId("run-a"), default));
        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>
    /// A model the provider's catalog does not list is reported as a missing model, not
    /// as whatever else went wrong.
    /// </summary>
    /// <remarks>
    /// The model check reads the catalog, so the two failures are adjacent: a catalog
    /// that could not be read at all must report its own code (covered above), and only a
    /// catalog that was read successfully may accuse the model.
    /// </remarks>
    [Fact]
    public async Task A_model_absent_from_a_healthy_catalog_reports_a_missing_model()
    {
        var cacheFile = await WriteModelsDevCacheAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash:free" }, default);
        var credentials = new KeyedCredentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System, cacheFile);
        using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(RunId("run-a"), default));
        Assert.Equal("model_unavailable", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>A model the catalog does list still dispatches, so the check cannot false-block.</summary>
    private async Task<string> WriteModelsDevCacheAsync()
    {
        Directory.CreateDirectory(_root);
        var entries = Enumerable.Range(0, 60)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}");
        var providers = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"},"
            + string.Join(",", entries) + "}";
        var cacheFile = Path.Combine(_root, "models-dev.json");
        await File.WriteAllTextAsync(cacheFile,
            $$"""{"fetched_at":"2099-01-01T00:00:00+00:00","payload":{{providers}}}""");
        return cacheFile;
    }

    /// <summary>A catalog endpoint that answers with a status the service has codes for.</summary>
    private sealed class FailingCatalogHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static async Task<ProviderBootstrapData> ReadAsync(AgentProviderSnapshotFactory factory, string run)
    {
        return await factory.GetAsync(RunId(run), default);
    }

    private static RunId RunId(string run) => new("run_" + new string(run.EndsWith('a') ? 'a' : 'b', 32));

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

    /// <summary>Any network fetch is a fail: the missing key/model must throw first.</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network fetch may happen before the credential/model check");
    }

    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class KeyedCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("key-one");
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
