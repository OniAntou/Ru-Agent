using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RuBackend.Compatibility;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class StatusEndpointsTests
{
    [Theory]
    [InlineData("/status")]
    [InlineData("/models?backend=OpenCode%20Zen")]
    public async Task AspNet_mode_maps_status_and_models_to_the_private_worker(string path)
    {
        var worker = WorkerReturning("{\"status\":\"ok\"}");
        var catchAll = new StubUpstreamHttpTransport(new HttpRequestException("catch-all"));
        using var factory = new BackendWebApplicationFactory(
            catchAll,
            runtimeWorkerClient: worker,
            statusOwnership: "aspnet");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, Assert.Single(worker.Requests).PathAndQuery);
        Assert.Null(catchAll.LastRequest);
    }

    [Fact]
    public async Task Status_returns_503_when_worker_is_not_ready()
    {
        using var factory = new BackendWebApplicationFactory(
            runtimeWorkerClient: new StubRuntimeWorkerClient(
                new HttpRequestException("private model path")),
            statusOwnership: "aspnet");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/status");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private model path", text, StringComparison.Ordinal);
    }

    private static StubRuntimeWorkerClient WorkerReturning(string body) => new(new UpstreamResponse(
        StatusCodes.Status200OK,
        "application/json",
        Encoding.UTF8.GetBytes(body)));
}
