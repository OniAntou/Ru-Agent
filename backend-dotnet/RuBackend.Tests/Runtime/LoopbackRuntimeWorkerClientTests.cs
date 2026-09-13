using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class LoopbackRuntimeWorkerClientTests
{
    [Fact]
    public async Task Adapter_sends_a_private_runtime_request_and_preserves_json_response()
    {
        var protocol = new StubRuntimeProtocolClient(new WorkerRuntimeResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-runtime"] = "worker",
            }));
        var adapter = new LoopbackRuntimeWorkerClient(protocol);

        var response = await adapter.SendAsync(
            new RuntimeWorkerRequest(
                HttpMethod.Post,
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes("{\"text\":\"Xin chào\"}")),
            CancellationToken.None);

        Assert.NotNull(protocol.LastRequest);
        Assert.Equal("POST", protocol.LastRequest!.Method);
        Assert.Equal("/voice/tts", protocol.LastRequest.PathAndQuery);
        Assert.Equal("application/json", protocol.LastRequest.ContentType);
        Assert.Equal(
            "{\"text\":\"Xin chào\"}",
            Encoding.UTF8.GetString(protocol.LastRequest.Body));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("worker", response.Headers!["x-runtime"]);
    }

    [Fact]
    public async Task Adapter_does_not_include_body_values_in_protocol_errors()
    {
        var protocol = new StubRuntimeProtocolClient(
            new WorkerProtocolException("runtime_failed"));
        var adapter = new LoopbackRuntimeWorkerClient(protocol);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => adapter.SendAsync(
            new RuntimeWorkerRequest(
                HttpMethod.Post,
                "/voice/transcribe",
                "application/json",
                Encoding.UTF8.GetBytes("{\"audio_b64\":\"private-audio\"}")),
            CancellationToken.None));

        Assert.DoesNotContain("private-audio", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("voice/transcribe", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class StubRuntimeProtocolClient : IRuntimeProtocolClient
    {
        private readonly WorkerRuntimeResponse? _response;
        private readonly Exception? _error;

        public StubRuntimeProtocolClient(WorkerRuntimeResponse response)
        {
            _response = response;
        }

        public StubRuntimeProtocolClient(Exception error)
        {
            _error = error;
        }

        public WorkerRuntimeRequest? LastRequest { get; private set; }

        public Task<WorkerRuntimeResponse> InvokeRuntimeAsync(
            WorkerRuntimeRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _error is null
                ? Task.FromResult(_response!)
                : Task.FromException<WorkerRuntimeResponse>(_error);
        }
    }
}
