using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Vts;

public sealed class VtsEndpointTests
{
    public static TheoryData<HttpMethod, string, string?> Routes => new()
    {
        { HttpMethod.Get, "/vts/mouse-look", null },
        { HttpMethod.Post, "/vts/mouse-look", "{\"enabled\":true,\"sensitivity\":1.0}" },
        { HttpMethod.Get, "/vts/overlay", null },
        { HttpMethod.Post, "/vts/overlay", "{\"enabled\":false}" },
        { HttpMethod.Get, "/vts/status", null },
        { HttpMethod.Post, "/vts/connect", null },
        { HttpMethod.Get, "/vts/config", null },
        { HttpMethod.Post, "/vts/config", "{\"sway_amplitude_idle\":10.0}" },
        { HttpMethod.Post, "/vts/activity", "{\"typing\":true}" },
        { HttpMethod.Post, "/vts/action", "{\"codes\":\"vts_01\"}" },
        { HttpMethod.Post, "/vts/speaking", "{\"speaking\":true}" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AspNet_mode_maps_each_VTS_route_to_the_runtime_worker(
        HttpMethod method,
        string path,
        string? body)
    {
        const string workerBody = "{\"status\":\"ok\",\"owner\":\"runtime-worker\"}";
        var worker = WorkerReturning(workerBody);
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(workerBody, await response.Content.ReadAsStringAsync());
        var forwarded = Assert.Single(worker.Requests);
        Assert.Equal(method, forwarded.Method);
        Assert.Equal(path, forwarded.PathAndQuery);
        Assert.Equal(body ?? string.Empty, Encoding.UTF8.GetString(forwarded.Body));
        Assert.Equal(body is null ? null : "application/json; charset=utf-8", forwarded.ContentType);
    }

    [Fact]
    public async Task AspNet_mode_returns_bounded_503_when_the_VTS_worker_is_unavailable()
    {
        var worker = new StubRuntimeWorkerClient(new HttpRequestException("private VTS detail"));
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/vts/status");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.DoesNotContain("private VTS detail", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_runs_before_VTS_worker_dispatch()
    {
        var worker = WorkerReturning("{\"status\":\"ok\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/vts/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Unknown_VTS_path_is_not_forwarded_or_dispatched()
    {
        var worker = WorkerReturning("{\"status\":\"unexpected\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/vts/not-a-real-route");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(worker.Requests);
    }

    private static StubRuntimeWorkerClient WorkerReturning(string body) =>
        new(new RuntimeWorkerResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes(body)));
}
