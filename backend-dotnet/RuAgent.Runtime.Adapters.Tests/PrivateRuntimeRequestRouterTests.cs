using System.Text;
using RuAgent.Runtime.Adapters.Runtime;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class PrivateRuntimeRequestRouterTests
{
    [Theory]
    [InlineData("GET", "/status")]
    [InlineData("GET", "/models?backend=Ollama")]
    [InlineData("POST", "/voice/tts")]
    public async Task Allowlisted_routes_are_forwarded(string method, string path)
    {
        var handler = new RecordingHandler();
        var router = new PrivateRuntimeRequestRouter(handler);

        var response = await router.InvokeAsync(
            new PrivateRuntimeRequest(method, path, "application/json", Encoding.UTF8.GetBytes("{}")),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(method, handler.LastRequest!.Method);
        Assert.Equal(path, handler.LastRequest.PathAndQuery);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unallowlisted_route_is_rejected_without_provider_invocation()
    {
        var handler = new RecordingHandler();
        var router = new PrivateRuntimeRequestRouter(handler);

        var error = await Assert.ThrowsAsync<PrivateRuntimeRequestException>(() => router.InvokeAsync(
            new PrivateRuntimeRequest("GET", "/settings", null, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None));

        Assert.Equal("unsupported_route", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Secret_like_json_keys_are_rejected_without_provider_invocation()
    {
        var handler = new RecordingHandler();
        var router = new PrivateRuntimeRequestRouter(handler);

        var error = await Assert.ThrowsAsync<PrivateRuntimeRequestException>(() => router.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes("{\"nested\":{\"api_key\":\"private\"}}")),
            CancellationToken.None));

        Assert.Equal("invalid_request", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Binary_response_requires_explicit_binary_announcement()
    {
        var handler = new RecordingHandler
        {
            Response = new PrivateRuntimeResponse(
                200,
                "audio/wav",
                new byte[] { 0x00, 0xFF },
                binary: false),
        };
        var router = new PrivateRuntimeRequestRouter(handler);

        var error = await Assert.ThrowsAsync<PrivateRuntimeRequestException>(() => router.InvokeAsync(
            new PrivateRuntimeRequest("POST", "/voice/tts", "application/json", Encoding.UTF8.GetBytes("{}")),
            CancellationToken.None));

        Assert.Equal("invalid_response", error.Code);
    }

    private sealed class RecordingHandler : IPrivateRuntimeRequestHandler
    {
        public int Calls { get; private set; }
        public PrivateRuntimeRequest? LastRequest { get; private set; }
        public PrivateRuntimeResponse Response { get; init; } = new(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"),
            binary: false);

        public Task<PrivateRuntimeResponse> InvokeAsync(
            PrivateRuntimeRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }
}
