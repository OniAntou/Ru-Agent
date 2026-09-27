using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Tests;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// Picking a reasoning level has to survive the whole request, not just render.
/// </summary>
/// <remarks>
/// The first attempt at this only filled <c>CatalogModel.Reasoning</c>, which is
/// what the picker reads. Every level showed up in the dropdown and every click on
/// one came straight back: SettingsTransitionService validates the choice through
/// <c>ProviderReasoningPreferences.Resolve</c>, which reads
/// <c>CatalogModel.WireOptions</c>, and that was still empty, so the save answered
/// "Invalid settings payload" and the button snapped back to "default".
///
/// These go through the real endpoint and the real transition service, because the
/// unit-level Resolve call passed while the feature was still broken end to end.
/// </remarks>
public sealed class ModelsDevReasoningSaveTests : IDisposable
{
    private const string Backend = "models.dev:openrouter";
    private const string OpenRouterApi = "https://openrouter.ai/api/v1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ru-reasoning-save-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_models_dev_level_can_be_picked_and_sticks()
    {
        await WriteModelsDevCatalogAsync();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: DataPaths.Resolve(_root, _root, _root, _root, Path.Combine(_root, "logs")),
            credentialStore: new OpenRouterCredential());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddHttpClient("ProviderCatalog")
                .ConfigurePrimaryHttpMessageHandler(() => new OpenRouterModelsHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var store = configured.Services.GetRequiredService<ISettingsStore>();
        await store.SaveAsync(
            (await store.ReadAsync(default)) with { Backend = Backend, Model = "vendor/model-a" }, default);

        var before = JsonNode.Parse(await client.GetStringAsync("/models"))!;
        Assert.True(
            before["status"]?.GetValue<string>() == "ok",
            $"status={before["status"]} error_code={before["error_code"]}");        Assert.Equal(
            ["low", "medium", "high"],
            before["catalog"]!.AsArray()[0]!["reasoning"]!["options"]!.AsArray().Select(n => n!.GetValue<string>()));

        using var saved = await client.PostAsJsonAsync("/settings", new
        {
            reasoning_effort = "high",
            reasoning_model = "vendor/model-a",
            reasoning_identity = before["identity"]!.GetValue<string>(),
        });
        var body = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;

        Assert.Equal("success", body["status"]?.GetValue<string>());
        Assert.Equal("high", JsonNode.Parse(await client.GetStringAsync("/models"))?["selected_effort"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_level_the_provider_never_published_is_still_refused()
    {
        await WriteModelsDevCatalogAsync();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: DataPaths.Resolve(_root, _root, _root, _root, Path.Combine(_root, "logs")),
            credentialStore: new OpenRouterCredential());
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddHttpClient("ProviderCatalog")
                .ConfigurePrimaryHttpMessageHandler(() => new OpenRouterModelsHandler())));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var store = configured.Services.GetRequiredService<ISettingsStore>();
        await store.SaveAsync(
            (await store.ReadAsync(default)) with { Backend = Backend, Model = "vendor/model-a" }, default);

        var catalog = JsonNode.Parse(await client.GetStringAsync("/models"))!;
        using var saved = await client.PostAsJsonAsync("/settings", new
        {
            reasoning_effort = "xhigh",
            reasoning_model = "vendor/model-a",
            reasoning_identity = catalog["identity"]!.GetValue<string>(),
        });

        Assert.Equal("error", JsonNode.Parse(await saved.Content.ReadAsStringAsync())?["status"]?.GetValue<string>());
        Assert.Equal("default", JsonNode.Parse(await client.GetStringAsync("/models"))?["selected_effort"]?.GetValue<string>());
    }

    /// <summary>
    /// A Models.dev catalog with enough providers to clear the minimum, and one
    /// model publishing an explicit effort list.
    /// </summary>
    private async Task WriteModelsDevCatalogAsync()
    {
        Directory.CreateDirectory(_root);
        var filler = string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $"\"filler{i:000}\":{{\"api\":\"https://filler{i:000}.example/v1\"}}"));
        const string OpenRouterEntry =
            "\"openrouter\":{\"api\":\"" + OpenRouterApi + "\",\"models\":{\"vendor/model-a\":"
            + "{\"reasoning\":true,\"reasoning_options\":"
            + "[{\"type\":\"effort\",\"values\":[\"low\",\"medium\",\"high\"]}]}}}";
        var payload = "{" + filler + "," + OpenRouterEntry + "}";
        await File.WriteAllTextAsync(
            Path.Combine(_root, "models-dev.json"),
            "{\"fetched_at\":\"2099-01-01T00:00:00+00:00\",\"payload\":" + payload + "}");
    }

    /// <remarks>
    /// Best-effort: the harness SQLite file can still be open when the test body
    /// returns, and a locked leftover temp directory is not a test failure.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class OpenRouterModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // What OpenRouter actually returns: ids, no reasoning metadata at all.
                Content = new StringContent("""{"data":[{"id":"vendor/model-a"}]}"""),
            });
    }

    private sealed class OpenRouterCredential : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(
                serviceName == "Ru" && accountName == "openrouter_api_key" ? "or-key" : null);

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
