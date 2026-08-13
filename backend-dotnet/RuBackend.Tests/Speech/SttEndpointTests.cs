using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Speech;

public sealed class SttEndpointTests
{
    [Fact]
    public async Task AspNet_mode_rejects_empty_audio_without_worker_call()
    {
        var worker = WorkerReturning("{\"status\":\"ok\",\"text\":\"unexpected\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "", sample_rate = 16000 });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Equal("audio is required", payload.GetProperty("message").GetString());
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task AspNet_mode_rejects_invalid_base64_without_worker_call()
    {
        var worker = WorkerReturning("{\"status\":\"ok\",\"text\":\"unexpected\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "not-base64!", sample_rate = 16000 });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("invalid audio payload", payload.GetProperty("message").GetString());
        Assert.Empty(worker.Requests);
    }

    [Theory]
    [InlineData(7999)]
    [InlineData(192001)]
    public async Task AspNet_mode_rejects_out_of_range_sample_rate(int sampleRate)
    {
        var worker = WorkerReturning("{\"status\":\"ok\",\"text\":\"unexpected\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "AA==", sample_rate = sampleRate });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task AspNet_mode_sends_valid_audio_to_the_runtime_worker()
    {
        const string workerBody = "{\"status\":\"ok\",\"text\":\"xin chao\"}";
        var worker = WorkerReturning(workerBody);
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "AAAAAA==", sample_rate = 16000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(workerBody, await response.Content.ReadAsStringAsync());
        var request = Assert.Single(worker.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/voice/transcribe", request.PathAndQuery);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal("AAAAAA==", document.RootElement.GetProperty("audio_b64").GetString());
    }

    [Fact]
    public async Task AspNet_mode_returns_503_when_the_runtime_worker_is_unavailable()
    {
        var worker = new StubRuntimeWorkerClient(new HttpRequestException("private worker detail"));
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "AAAAAA==", sample_rate = 16000 });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.DoesNotContain("private worker detail", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_runs_before_STT_validation()
    {
        var worker = WorkerReturning("{\"status\":\"ok\"}");
        using var factory = new BackendWebApplicationFactory(runtimeWorkerClient: worker);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/voice/transcribe",
            new { audio_b64 = "", sample_rate = 16000 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(worker.Requests);
    }

    private static StubRuntimeWorkerClient WorkerReturning(string body) =>
        new(new RuntimeWorkerResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes(body)));
}
