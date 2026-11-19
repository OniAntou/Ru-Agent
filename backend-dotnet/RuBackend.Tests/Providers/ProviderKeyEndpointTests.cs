using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ProviderKeyEndpointTests
{
    [Fact]
    public async Task Credential_validation_requires_the_host_bearer()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/settings/provider/key", new
        {
            models_dev_provider_id = "models.dev:synthetic",
            models_dev_api_key = "synthetic",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Credential_route_rejects_mixed_settings_before_dispatch()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var response = await client.PostAsJsonAsync("/settings/provider/key", new
        {
            models_dev_provider_id = "models.dev:synthetic",
            models_dev_api_key = "synthetic",
            backend_choice = "models.dev:synthetic",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_hosts_settings_save_seeds_the_shared_redactor()
    {
        using var factory = new BackendWebApplicationFactory();
        var transition = factory.Services.GetRequiredService<RuBackend.Business.Settings.ISettingsTransitionService>();
        // The singleton every payload store and live publisher redacts with.
        var secrets = factory.Services.GetRequiredService<RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor>();

        var result = await transition.ApplyAsync(
            new System.Text.Json.Nodes.JsonObject { ["custom_api_key"] = "synthetic-stored-key" }, default);

        Assert.True(result.Success, result.Code);
        Assert.Equal(RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Replacement,
            secrets.Redact("synthetic-stored-key"));

        // The real settings store keeps the key's status and GET /settings returns it.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        using var settings = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/settings"));
        Assert.Equal("unchecked", settings.RootElement.GetProperty("provider_key_status")
            .GetProperty("Custom OpenAI").GetProperty("state").GetString());
        Assert.DoesNotContain("synthetic-stored-key", settings.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixed_provider_key_route_carries_only_the_key()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var response = await client.PostAsJsonAsync("/settings/provider/key", new
        {
            openai_api_key = "synthetic",
            backend = "openai",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A custom key may be checked, but never together with the endpoint it goes to.
        response = await client.PostAsJsonAsync("/settings/provider/key", new
        {
            custom_api_key = "synthetic",
            custom_url = "https://elsewhere.example/v1",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
