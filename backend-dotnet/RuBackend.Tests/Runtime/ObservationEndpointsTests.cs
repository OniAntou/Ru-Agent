using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class ObservationEndpointsTests
{
    public static TheoryData<HttpMethod, string, string?> Routes => new()
    {
        { HttpMethod.Post, "/observation/preflight", null },
        { HttpMethod.Get, "/observation/vision-config", null },
        { HttpMethod.Post, "/observation/react", "{\"observation_id\":\"obs-1\"}" },
        { HttpMethod.Post, "/observation/cancel", "{\"observation_id\":\"obs-1\"}" },
        { HttpMethod.Post, "/observation/ack", "{\"observation_id\":\"obs-1\",\"played\":false}" },
        { HttpMethod.Post, "/observation/start", null },
        { HttpMethod.Post, "/observation/stop", null },
        { HttpMethod.Get, "/observation/status", null },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AspNet_mode_maps_each_observation_route_to_the_private_worker(
        HttpMethod method,
        string path,
        string? body)
    {
        var worker = WorkerReturning("{\"status\":\"disabled\",\"active\":false}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(worker.Requests);
        Assert.Equal(method, forwarded.Method);
        Assert.Equal(path, forwarded.PathAndQuery);
    }

    [Fact]
    public async Task Disabled_state_is_preserved_exactly()
    {
        const string disabled = "{\"status\":\"disabled\",\"active\":false,\"engine\":\"disabled\"}";
        using var factory = new BackendWebApplicationFactory(
            runtimeWorkerClient: WorkerReturning(disabled));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/observation/status");

        Assert.Equal(disabled, await response.Content.ReadAsStringAsync());
    }

    private static StubRuntimeWorkerClient WorkerReturning(string body) => new(new RuntimeWorkerResponse(
        StatusCodes.Status200OK,
        "application/json",
        Encoding.UTF8.GetBytes(body)));
}
