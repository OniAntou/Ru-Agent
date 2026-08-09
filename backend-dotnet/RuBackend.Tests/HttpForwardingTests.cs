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

    [Fact]
    public async Task GenericRoutePreservesMethodEscapedPathQueryBodyAndHeaders()
    {
        var transport = new StubUpstreamHttpTransport(request => new UpstreamResponse(
            StatusCodes.Status201Created,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes("{\"active\":true}"),
            new Dictionary<string, string> { ["ETag"] = "\"revision-2\"" }));
        using var factory = new BackendWebApplicationFactory(transport);
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/sessions/room%2Fone/activate?first=1&first=2");
        request.Content = new StringContent(
            "{\"active\":true}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("{\"active\":true}", body);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.TryGetValues("ETag", out var values));
        Assert.Equal("\"revision-2\"", values.Single());
        Assert.NotNull(transport.LastRequest);
        Assert.Equal(HttpMethod.Put, transport.LastRequest!.Method);
        Assert.Equal(
            "/sessions/room%2Fone/activate?first=1&first=2",
            transport.LastRequest.PathAndQuery);
        Assert.Equal("Bearer test-secret", transport.LastRequest.Headers["Authorization"]);
        Assert.StartsWith("application/json", transport.LastRequest.Headers["Content-Type"]);
        Assert.Equal(
            Encoding.UTF8.GetBytes("{\"active\":true}"),
            transport.LastRequest.Body);
    }

    [Fact]
    public async Task UnknownPathIsStillForwarded()
    {
        var transport = new StubUpstreamHttpTransport(request => new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes("{\"future\":true}")));
        using var factory = new BackendWebApplicationFactory(transport);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/future-contract?revision=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/future-contract?revision=7", transport.LastRequest?.PathAndQuery);
    }
}
