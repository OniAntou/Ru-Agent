using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RuBackend.Compatibility;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests;

public sealed class HttpForwardingTests
{
    [Fact]
    public async Task SettingsPreservesUpstreamStatusAndJson()
    {
        using var factory = new BackendWebApplicationFactory(
            new StubUpstreamHttpTransport(
                new UpstreamResponse(
                    StatusCodes.Status200OK,
                    "application/json",
                    Encoding.UTF8.GetBytes("{\"backend\":\"llama.cpp\"}"))));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"backend\":\"llama.cpp\"}", json);
    }

    [Fact]
    public async Task UpstreamFailureMakesReadinessUnavailable()
    {
        using var factory = new BackendWebApplicationFactory(
            new StubUpstreamHttpTransport(new HttpRequestException("upstream down")));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SettingsPreservesSafeUpstreamHeaders()
    {
        using var factory = new BackendWebApplicationFactory(
            new StubUpstreamHttpTransport(
                new UpstreamResponse(
                    StatusCodes.Status200OK,
                    "application/json",
                    Encoding.UTF8.GetBytes("{}"),
                    new Dictionary<string, string> { ["ETag"] = "\"revision-1\"" })));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");

        Assert.True(response.Headers.TryGetValues("ETag", out var values));
        Assert.Equal("\"revision-1\"", values.Single());
    }
}
