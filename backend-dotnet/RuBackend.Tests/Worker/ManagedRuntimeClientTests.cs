using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class ManagedRuntimeClientTests
{
    [Fact]
    public async Task Client_keeps_private_runtime_request_redacted_and_preserves_response()
    {
        var protocol = new RecordingProtocolClient(new WorkerRuntimeResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes("{\"status\":\"ok\"}")));
        var client = new LoopbackRuntimeWorkerClient(protocol);

        var response = await client.SendAsync(
            new RuntimeWorkerRequest(
                HttpMethod.Post,
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes("{\"text\":\"Xin chào\"}")),
            CancellationToken.None);

        Assert.Equal("POST", protocol.Request!.Method);
        Assert.Equal("/voice/tts", protocol.Request.PathAndQuery);
        Assert.Equal("{\"text\":\"Xin chào\"}", Encoding.UTF8.GetString(protocol.Request.Body));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Client_does_not_expose_worker_details_when_private_request_fails()
    {
        var protocol = new RecordingProtocolClient(new WorkerProtocolException("runtime_request_failed"));
        var client = new LoopbackRuntimeWorkerClient(protocol);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
            new RuntimeWorkerRequest(
                HttpMethod.Post,
                "/voice/transcribe",
                "application/json",
                Encoding.UTF8.GetBytes("{\"audio_b64\":\"private-audio\"}")),
            CancellationToken.None));

        Assert.DoesNotContain("private-audio", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/voice/transcribe", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingProtocolClient : IRuntimeProtocolClient
    {
        private readonly WorkerRuntimeResponse? _response;
        private readonly Exception? _error;

        public RecordingProtocolClient(WorkerRuntimeResponse response) => _response = response;

        public RecordingProtocolClient(Exception error) => _error = error;

        public WorkerRuntimeRequest? Request { get; private set; }

        public Task<WorkerRuntimeResponse> InvokeRuntimeAsync(
            WorkerRuntimeRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return _error is null
                ? Task.FromResult(_response!)
                : Task.FromException<WorkerRuntimeResponse>(_error);
        }
    }
}
