using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed partial class BusinessEndpointTests
{
    [Fact]
    public async Task Retired_session_exchange_routes_are_not_registered_and_import_creates_no_session()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var export = await client.GetAsync("/sessions/anything/export?harness=codex");
        using var import = await client.PostAsJsonAsync(
            "/sessions/import",
            new { harness = "codex", content = "{}" });

        // The global OPTIONS wildcard makes removed routes return 405,
        // matching the existing retired /chat route rather than adding tombstones.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, export.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, import.StatusCode);
        var patterns = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        Assert.DoesNotContain("/sessions/{sessionId}/export", patterns);
        Assert.DoesNotContain("/sessions/import", patterns);
        Assert.Empty(await factory.Services.GetRequiredService<ISessionStore>()
            .ListAsync(CancellationToken.None));
    }
}
