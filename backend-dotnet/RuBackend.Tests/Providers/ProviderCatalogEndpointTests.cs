using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ProviderCatalogEndpointTests
{
    [Fact]
    public async Task Models_returns_the_provider_catalog_not_the_configured_worker_model()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new SingleCredential("openai_api_key", "openai-key"));
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var body = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;
        Assert.Equal("gpt-5.6-sol", body["models"]?[0]?.GetValue<string>());
        Assert.Equal("gpt-5.6-terra", body["models"]?[1]?.GetValue<string>());
        Assert.NotNull(body["identity"]);
    }

    [Fact]
    public async Task Models_returns_the_catalog_for_an_inactive_provider_with_a_credential()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new SingleCredential("openai_api_key", "openai-key"));
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;

        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.Equal("openai", body["backend"]!.GetValue<string>());
        Assert.Equal("gpt-5.6-sol", body["models"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Models_for_an_inactive_provider_without_a_credential_fails_closed()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new EmptyCredentials());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler(HttpStatusCode.Unauthorized))));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;

        Assert.Equal("error", body["status"]!.GetValue<string>());
        Assert.Equal("catalog_unauthorized", body["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Models_for_a_signed_out_provider_fails_closed_without_fetching()
    {
        var handler = new CatalogHandler();
        using var factory = new BackendWebApplicationFactory(credentialStore: new EmptyCredentials());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;

        Assert.Equal("error", body["status"]!.GetValue<string>());
        Assert.Equal("catalog_unauthorized", body["error_code"]!.GetValue<string>());
        Assert.Empty(body["models"]!.AsArray());
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Models_rejects_an_unknown_backend_alias()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new EmptyCredentials());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/models?backend=banana"))!;

        Assert.Equal("error", body["status"]!.GetValue<string>());
        Assert.Equal("provider_not_active", body["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ModelsDev_lists_providers_from_the_cached_list()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new EmptyCredentials());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new ModelsDevHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/models-dev"))!;

        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.Equal("openrouter", body["providers"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("https://openrouter.ai/api/v1", body["providers"]![0]!["base_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task ModelsDevKeys_lists_only_ids_with_a_stored_key()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new SingleCredential("openrouter_api_key", "router-secret"));
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new TwoProviderModelsDevHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var body = JsonNode.Parse(await client.GetStringAsync("/settings/models-dev-keys"))!;

        Assert.Equal("ok", body["status"]!.GetValue<string>());
        var ids = body["provider_ids"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(["openrouter"], ids);
    }

    private sealed class TwoProviderModelsDevHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"openrouter":{"name":"OpenRouter","api":"https://openrouter.ai/api/v1"},"zai":{"name":"Z.AI","api":"https://api.z.ai/api/coding/paas/v4"}}""") });
    }

    private sealed class ModelsDevHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://models.dev/api.json", request.RequestUri?.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"openrouter":{"name":"OpenRouter","api":"https://openrouter.ai/api/v1"}}""") });
        }
    }

    private sealed class CatalogHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("""
                {"data":[{"id":"gpt-5.6-sol"},{"id":"gpt-5.6-terra"}]}
                """) });
        }
    }

    private sealed class SingleCredential : ICredentialStore
    {
        private readonly string _account;
        private readonly string _secret;

        public SingleCredential(string account, string secret)
        {
            _account = account;
            _secret = secret;
        }

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(
                string.Equals(serviceName, "Ru", StringComparison.Ordinal) &&
                string.Equals(accountName, _account, StringComparison.Ordinal) ? _secret : null);

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Reasoning_save_validates_identity_and_preserves_per_model_choice()
    {
        using var factory = new BackendWebApplicationFactory(credentialStore: new SingleCredential("openai_api_key", "openai-key"));
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("ProviderCatalog").ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var store = configured.Services.GetRequiredService<ISettingsStore>();
        await store.SaveAsync((await store.ReadAsync(default)) with { OpenAiModel = "gpt-5.6-sol" }, default);
        var catalog = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;
        using var invalid = await client.PostAsJsonAsync("/settings", new
        {
            reasoning_effort = "high", reasoning_model = catalog["selected_model"]!.GetValue<string>(),
            reasoning_identity = "old-identity",
        });
        var invalidBody = JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!;
        Assert.Equal("error", invalidBody["status"]?.GetValue<string>());
        using var valid = await client.PostAsJsonAsync("/settings", new
        {
            reasoning_effort = "high", reasoning_model = "gpt-5.6-sol",
            reasoning_identity = catalog["identity"]!.GetValue<string>(),
        });
        Assert.Equal("success", JsonNode.Parse(await valid.Content.ReadAsStringAsync())?["status"]?.GetValue<string>());
        Assert.Equal("high", JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))?["selected_effort"]?.GetValue<string>());
        await store.SaveAsync((await store.ReadAsync(default)) with { OpenAiModel = "gpt-5.6-terra" }, default);
        catalog = JsonNode.Parse(await client.GetStringAsync("/models?backend=openai"))!;
        Assert.Equal("default", catalog["selected_effort"]?.GetValue<string>());
        using var reset = await client.PostAsJsonAsync("/settings", new
        {
            reasoning_effort = "default", reasoning_model = catalog["selected_model"]!.GetValue<string>(),
            reasoning_identity = catalog["identity"]!.GetValue<string>(),
        });
        var resetBody = JsonNode.Parse(await reset.Content.ReadAsStringAsync())!;
        Assert.Equal("success", resetBody["status"]?.GetValue<string>());
    }

    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
