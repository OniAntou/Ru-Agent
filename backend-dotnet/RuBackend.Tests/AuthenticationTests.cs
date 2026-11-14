using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RuBackend.Configuration;
using RuBackend.Security;
using Xunit;

namespace RuBackend.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task MissingBearerTokenIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidBearerTokenIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidBearerTokenReachesStatusEndpoint()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Backend is running", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "OPTIONS" }, true)]
    [InlineData(new[] { "GET", "OPTIONS" }, false)]
    [InlineData(new string[0], false)]
    public async Task Unauthenticated_options_reaches_only_an_options_only_endpoint(string[] methods, bool expectedPassThrough)
    {
        // The bypass exists for the bodiless OPTIONS catch-all. A route that also answers
        // another method (or any method) must keep the bearer requirement, even off the
        // sensitive-prefix list.
        var reachedNext = false;
        var middleware = new DesktopTokenMiddleware(
            _ => { reachedNext = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new BackendOptions { DesktopToken = "synthetic-token" }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Options;
        context.Request.Path = "/models";
        context.Response.Body = new MemoryStream();
        var metadata = methods.Length == 0
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(new HttpMethodMetadata(methods));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "probe"));

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedPassThrough, reachedNext);
        Assert.Equal(expectedPassThrough ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }
}
