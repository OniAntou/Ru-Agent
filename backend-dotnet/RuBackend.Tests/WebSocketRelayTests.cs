using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests;

public sealed class WebSocketRelayTests
{
    [Fact]
    public async Task RelayPreservesTextThenBinaryVoiceFrames()
    {
        using var fixture = await WebSocketRelayFixture.StartAsync();
        using var client = await fixture.ConnectClientAsync("/voice/stream?token=test-secret");

        await fixture.Upstream.SendTextAsync("{\"type\":\"audio\"}");
        await fixture.Upstream.SendBinaryAsync(new byte[] { 1, 2, 3 });

        var text = await client.ReceiveTextAsync();
        var binary = await client.ReceiveBinaryAsync();

        Assert.Equal("{\"type\":\"audio\"}", text);
        Assert.Equal(new byte[] { 1, 2, 3 }, binary);
        Assert.Equal("/voice/stream", fixture.UpstreamRequestUri?.AbsolutePath);
        Assert.Equal("?token=test-secret", fixture.UpstreamRequestUri?.Query);
    }

    [Fact]
    public async Task InvalidWebSocketTokenIsRejectedBeforeUpstreamConnect()
    {
        using var fixture = await WebSocketRelayFixture.StartAsync();

        using var response = await fixture.CreateHttpClient().GetAsync(
            "/voice/stream?token=wrong-token");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpstreamCloseIsPropagatedToClient()
    {
        using var fixture = await WebSocketRelayFixture.StartAsync();
        using var client = await fixture.ConnectClientAsync("/chat?token=test-secret");

        await fixture.Upstream.CloseOutputAsync();

        Assert.Equal(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            await client.ReceiveCloseAsync());
    }
}
