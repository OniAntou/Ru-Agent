using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RuBackend.Compatibility;
using RuBackend.Configuration;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class FastApiRuntimeWorkerClientTests
{
    [Fact]
    public async Task Adds_worker_auth_and_preserves_request_body()
    {
        var transport = new StubUpstreamHttpTransport(new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            "{}"u8.ToArray()));
        var client = new FastApiRuntimeWorkerClient(
            transport,
            Options.Create(new BackendOptions { DesktopToken = "worker-secret" }));
        var body = "{\"audio_b64\":\"AA==\",\"sample_rate\":16000}"u8.ToArray();

        await client.SendAsync(
            new RuntimeWorkerRequest(
                HttpMethod.Post,
                "/voice/transcribe",
                "application/json",
                body),
            CancellationToken.None);

        Assert.NotNull(transport.LastRequest);
        Assert.Equal(HttpMethod.Post, transport.LastRequest.Method);
        Assert.Equal("/voice/transcribe", transport.LastRequest.PathAndQuery);
        Assert.Equal("Bearer worker-secret", transport.LastRequest.Headers["Authorization"]);
        Assert.Equal("application/json", transport.LastRequest.Headers["Content-Type"]);
        Assert.Equal(body, transport.LastRequest.Body);
    }

    [Fact]
    public async Task Preserves_worker_response_without_inspecting_private_content()
    {
        var expected = new UpstreamResponse(
            StatusCodes.Status409Conflict,
            "application/json",
            "{\"status\":\"error\"}"u8.ToArray(),
            new Dictionary<string, string> { ["Retry-After"] = "1" });
        var client = new FastApiRuntimeWorkerClient(
            new StubUpstreamHttpTransport(expected),
            Options.Create(new BackendOptions { DesktopToken = "worker-secret" }));

        var actual = await client.SendAsync(
            new RuntimeWorkerRequest(HttpMethod.Get, "/vts/status", null, []),
            CancellationToken.None);

        Assert.Same(expected, actual);
    }
}
