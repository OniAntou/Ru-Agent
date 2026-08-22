using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BrokerHostTests
{
    [Fact]
    public async Task Health_requires_the_descriptor_bearer()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var client = new HttpClient();

        using var unauthorized = await client.GetAsync(fixture.Host.BaseUrl + "/health");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Host.BaseUrl + "/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            fixture.Token);
        using var authorized = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Contains("ready", await authorized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_websocket_answers_health_with_the_private_protocol()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);
        var request = new AgentEnvelope(
            1,
            "health-1",
            AgentMessageTypes.Health,
            "session-1",
            "corr-1",
            1_000,
            new System.Text.Json.Nodes.JsonObject());
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(request));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation.Token);

        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellation.Token);
        var reply = AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));

        Assert.Equal(AgentMessageTypes.HealthOk, reply.MessageType);
        Assert.Equal(request.RequestId, reply.RequestId);
        Assert.Equal(request.CorrelationId, reply.CorrelationId);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private HostFixture(string root, string token, BrokerHost host)
        {
            Root = root;
            Token = token;
            Host = host;
        }

        public string Root { get; }
        public string Token { get; }
        public BrokerHost Host { get; }

        public static async Task<HostFixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-agent-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var token = "broker-token-" + Guid.NewGuid().ToString("N");
            var descriptorPath = Path.Combine(root, "broker.json");
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                token,
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

            var host = await BrokerHost.StartAsync(descriptorPath);
            return new HostFixture(root, token, host);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static int GetFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
