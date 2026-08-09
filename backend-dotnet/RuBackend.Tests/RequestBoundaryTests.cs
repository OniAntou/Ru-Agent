using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace RuBackend.Tests;

public sealed class RequestBoundaryTests
{
    [Fact]
    public async Task OptionsRequestRemainsUnauthenticatedAndReturnsNoContent()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/status"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/settings")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        request.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;

        using var response = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
    }
}
