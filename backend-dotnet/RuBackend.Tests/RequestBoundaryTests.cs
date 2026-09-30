using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RuAgent.SessionInterchange;
using RuBackend.Configuration;
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

    /// <summary>
    /// The body-bound layering, pinned where it can actually be observed: Kestrel is the
    /// outer wall, above the route bound, which is above the endpoint's own reader. The
    /// test server does not enforce Kestrel's limit itself, so this asserts the app
    /// *wires* Kestrel above every application cap — a default Kestrel bound sits below
    /// the import route's and would pre-empt the endpoint's import_too_large 413.
    /// </summary>
    [Fact]
    public void Kestrel_bound_is_wired_above_every_application_body_bound()
    {
        using var factory = new BackendWebApplicationFactory();
        var kestrelBound = factory.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value
            .Limits
            .MaxRequestBodySize;

        var applicationCaps = new BackendOptions();
        Assert.Equal(BackendLimits.KestrelMaxRequestBodyBytes, kestrelBound);
        Assert.True(kestrelBound > applicationCaps.SessionImportMaxRequestBodyBytes);
        Assert.True(kestrelBound > applicationCaps.AgentRunMaxRequestBodyBytes);

        // Within the application: route bound > endpoint body bound > content cap.
        Assert.True(applicationCaps.SessionImportMaxRequestBodyBytes > SessionInterchangeLimits.MaxImportBodyBytes);
        Assert.True(SessionInterchangeLimits.MaxImportBodyBytes > SessionInterchangeLimits.MaxImportContentBytes);
    }
}
