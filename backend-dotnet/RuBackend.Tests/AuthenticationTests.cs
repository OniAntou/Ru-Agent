using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace RuBackend.Tests;

public sealed class AuthenticationTests : IClassFixture<BackendWebApplicationFactory>
{
    private readonly BackendWebApplicationFactory _factory;

    public AuthenticationTests(BackendWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MissingBearerTokenIsRejected()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidBearerTokenIsRejected()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidBearerTokenReachesStatusEndpoint()
    {
        using var client = _factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Backend is running", body, StringComparison.Ordinal);
    }
}
