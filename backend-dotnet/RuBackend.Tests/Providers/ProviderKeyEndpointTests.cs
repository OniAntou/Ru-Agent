using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
}
