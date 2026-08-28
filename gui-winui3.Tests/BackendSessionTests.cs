using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendSessionTests
{
    [Fact]
    public void BackendSession_builds_http_and_voice_endpoint_from_a_loopback_port()
    {
        var session = BackendSession.FromLoopback(18001, "desktop-secret");

        Assert.Equal(new Uri("http://127.0.0.1:18001/"), session.HttpBaseUri);
        Assert.Equal(
            new Uri("ws://127.0.0.1:18001/voice/stream?token=desktop-secret"),
            session.VoiceStreamUri);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void BackendSession_rejects_ports_outside_dynamic_loopback_range(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackendSession.FromLoopback(port, "secret"));
    }
}
