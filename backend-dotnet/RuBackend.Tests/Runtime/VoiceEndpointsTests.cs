using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class VoiceEndpointsTests
{
    public static TheoryData<HttpMethod, string, string?> Routes => new()
    {
        { HttpMethod.Get, "/voice/status", null },
        { HttpMethod.Post, "/voice/latency", "{\"outcome\":\"completed\"}" },
        { HttpMethod.Post, "/voice/tts", "{\"text\":\"synthetic\",\"speed\":1.0}" },
        { HttpMethod.Post, "/voice/warmup", null },
        { HttpMethod.Post, "/voice/release", null },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AspNet_mode_maps_each_voice_HTTP_route_to_the_private_worker(
        HttpMethod method,
        string path,
        string? body)
    {
        var worker = JsonWorker();
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
        Assert.Equal(body ?? string.Empty, Encoding.UTF8.GetString(forwarded.Body));
    }

    [Fact]
    public async Task Tts_preserves_non_empty_binary_audio_and_content_type()
    {
        var worker = new StubRuntimeWorkerClient(new RuntimeWorkerResponse(
            200,
            "audio/wav",
            Encoding.ASCII.GetBytes("RIFF-wave")));
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync(
            "/voice/tts",
            new StringContent("{\"text\":\"synthetic\"}", Encoding.UTF8, "application/json"));

        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RIFF-wave", Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Worker_failure_returns_a_bounded_503()
    {
        var worker = new StubRuntimeWorkerClient(new HttpRequestException("private voice detail"));
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/voice/status");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private voice detail", text, StringComparison.Ordinal);
    }

    private static StubRuntimeWorkerClient JsonWorker() => new(new RuntimeWorkerResponse(
        StatusCodes.Status200OK,
        "application/json",
        "{\"status\":\"ok\"}"u8.ToArray()));
}
