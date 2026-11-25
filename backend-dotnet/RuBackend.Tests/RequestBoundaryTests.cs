using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RuBackend.Configuration;
using RuBackend.Security;
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

    [Theory]
    [InlineData("/agent/v1/runs")]
    [InlineData("/agent/v1/runs/selected")]
    public async Task Agent_run_routes_share_the_larger_body_bound(string path)
    {
        // Both create routes accept the optional image payloads and share one
        // endpoint reader; the per-route middleware must not cap the selected route
        // at the general 8 MiB bound.
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new ByteArrayContent(new byte[9 * 1024 * 1024]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_websocket_upgrade_is_rejected_as_unauthorized()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/agent/v1/runs");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    /// <summary>The test pins Kestrel above the largest application request bound.</summary>
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
        Assert.True(kestrelBound > applicationCaps.AgentRunMaxRequestBodyBytes);
    }

    /// <summary>
    /// A raised run-body cap must raise the transport bound with it; a fixed
    /// 30 MiB Kestrel limit would reject the route before it could answer its own
    /// bounded 413 code.
    /// </summary>
    [Fact]
    public void Configured_larger_run_cap_raises_the_transport_bound()
    {
        const long configuredCap = 64L * 1024 * 1024;
        using var factory = new BackendWebApplicationFactory(
            settings: new Dictionary<string, string>
            {
                ["RuBackend:AgentRunMaxRequestBodyBytes"] = configuredCap.ToString(),
            });
        var kestrelBound = factory.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value
            .Limits
            .MaxRequestBodySize;

        Assert.Equal(BackendLimits.KestrelMaxRequestBodyBytesFor(configuredCap), kestrelBound);
        Assert.True(kestrelBound > configuredCap);
        Assert.True(kestrelBound > BackendLimits.KestrelMaxRequestBodyBytes);
    }

    /// <summary>The generic cap is configurable too and must raise the same bound.</summary>
    [Fact]
    public void Configured_larger_generic_cap_raises_the_transport_bound()
    {
        const long configuredCap = 48L * 1024 * 1024;
        using var factory = new BackendWebApplicationFactory(
            settings: new Dictionary<string, string>
            {
                ["RuBackend:MaxRequestBodyBytes"] = configuredCap.ToString(),
            });
        var kestrelBound = factory.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value
            .Limits
            .MaxRequestBodySize;

        Assert.Equal(BackendLimits.KestrelMaxRequestBodyBytesFor(configuredCap), kestrelBound);
        Assert.True(kestrelBound > configuredCap);
    }

    /// <summary>
    /// Routing ignores trailing slashes, so the trailing-slash form of a create route
    /// reaches the same endpoint and must inherit the larger run cap. Before the
    /// normalization, "/agent/v1/runs/" fell back to the 8 MiB generic cap and answered
    /// 413 for a body the documented route accepts.
    /// </summary>
    [Theory]
    [InlineData("/agent/v1/runs/")]
    [InlineData("/agent/v1/runs/selected/")]
    public async Task Agent_run_routes_share_the_larger_body_bound_with_a_trailing_slash(string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new ByteArrayContent(new byte[9 * 1024 * 1024]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(path, content);

        // The body is above the generic 8 MiB cap and below the run cap: the request
        // reaches the handler (and fails JSON parsing), never the generic 413.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The trailing-slash normalization must stay route-exact: a lookalike path that
    /// no create endpoint serves keeps the small generic cap.
    /// </summary>
    [Theory]
    [InlineData("/agent/v1/runs", true)]
    [InlineData("/agent/v1/runs/", true)]
    [InlineData("/agent/v1/runs//", true)]
    [InlineData("/agent/v1/runs/selected", true)]
    [InlineData("/agent/v1/runs/selected/", true)]
    [InlineData("/agent/v1/runsXYZ", false)]
    [InlineData("/agent/v1/runs/selected/extra", false)]
    [InlineData("/agent/v1/runs/run_0123456789abcdef0123456789abcdef/steers", true)]
    [InlineData("/agent/v1/runs/run_0123456789abcdef0123456789abcdef/steers/", true)]
    [InlineData("/agent/v1/runs//steers", false)]
    [InlineData("/agent/v1/runs/a/b/steers", false)]
    [InlineData("/agent/v1/runs/run_x/steers/extra", false)]
    [InlineData("/agent/v1/runs/run_x/steersXYZ", false)]
    public async Task Request_size_middleware_matches_only_the_run_create_route_family(
        string path,
        bool agentRunCap)
    {
        var reachedNext = false;
        var middleware = new RequestSizeMiddleware(
            _ => { reachedNext = true; return Task.CompletedTask; },
            Options.Create(new BackendOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentLength = 9 * 1024 * 1024;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(agentRunCap, reachedNext);
        Assert.Equal(
            agentRunCap ? StatusCodes.Status200OK : StatusCodes.Status413PayloadTooLarge,
            context.Response.StatusCode);
    }
}
