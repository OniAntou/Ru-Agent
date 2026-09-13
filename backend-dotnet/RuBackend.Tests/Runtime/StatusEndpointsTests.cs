using System.Net;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class StatusEndpointsTests
{
    [Fact]
    public async Task AspNet_mode_serves_status_from_the_host_settings()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("backend").GetString()));
    }

    [Fact]
    public async Task Status_requires_the_desktop_bearer()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_rejects_an_oversized_GET_body_before_dispatch()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status")
        {
            Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
