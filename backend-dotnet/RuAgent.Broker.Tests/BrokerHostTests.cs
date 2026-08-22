using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Desktop;
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

    [Fact]
    public async Task Authenticated_session_can_use_a_registered_read_tool()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-1", AgentMessageTypes.Hello, "session-1", "corr-1", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-1",
                AgentMessageTypes.SessionOpen,
                "session-1",
                "corr-1",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.read"),
                }), cancellation.Token);
        var opened = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("succeeded", opened.Payload["status"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "tool-1",
                AgentMessageTypes.ToolRequest,
                "session-1",
                "corr-1",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "files.list",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "src" },
                }), cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.ToolResult, result.MessageType);
        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.NotNull(result.Payload["output"]!["entries"]);
    }

    [Fact]
    public async Task Authenticated_session_can_execute_volume_with_a_fake_controller()
    {
        var controller = new FakeVolumeController(level: 40);
        await using var fixture = await HostFixture.StartAsync(controller);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-volume", AgentMessageTypes.Hello, "session-volume", "corr-volume", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-volume",
                AgentMessageTypes.SessionOpen,
                "session-volume",
                "corr-volume",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("system.write"),
                }),
            cancellation.Token);
        var opened = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("succeeded", opened.Payload["status"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "volume-1",
                AgentMessageTypes.ToolRequest,
                "session-volume",
                "corr-volume",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "system.volume",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["operation"] = "set",
                        ["level"] = 2,
                    },
                }),
            cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.Equal(2, result.Payload["output"]!["level"]!.GetValue<int>());
        Assert.True(result.Payload["output"]!["verified"]!.GetValue<bool>());
        Assert.Equal([("level", 2)], controller.Writes);
    }

    [Fact]
    public async Task Authenticated_session_can_run_an_allowlisted_terminal_command()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-terminal", AgentMessageTypes.Hello, "session-terminal", "corr-terminal", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-terminal",
                AgentMessageTypes.SessionOpen,
                "session-terminal",
                "corr-terminal",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("terminal.execute"),
                }),
            cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "terminal-1",
                AgentMessageTypes.ToolRequest,
                "session-terminal",
                "corr-terminal",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "terminal.run",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["executable"] = "dotnet",
                        ["arguments"] = new System.Text.Json.Nodes.JsonArray("--version"),
                        ["working_directory"] = ".",
                    },
                }),
            cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.Contains("10.", result.Payload["output"]!["stdout"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_response_runs_the_original_shell_request_once()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-shell", AgentMessageTypes.Hello, "session-shell", "corr-shell", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-shell",
                AgentMessageTypes.SessionOpen,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("terminal.execute"),
                }),
            cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "shell-request-1",
                AgentMessageTypes.ToolRequest,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "terminal.shell",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["shell"] = "powershell",
                        ["working_directory"] = ".",
                        ["script"] = "Get-Date",
                    },
                }),
            cancellation.Token);
        var pending = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("approval_required", pending.Payload["status"]!.GetValue<string>());
        var approvalId = pending.Payload["output"]!["approval_id"]!.GetValue<string>();
        Assert.DoesNotContain("Get-Date", pending.Payload.ToJsonString(), StringComparison.Ordinal);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "approval-response-1",
                AgentMessageTypes.ApprovalResponse,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var approved = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("succeeded", approved.Payload["status"]!.GetValue<string>());
        Assert.Contains("", approved.Payload["output"]!["stdout"]!.GetValue<string>(), StringComparison.Ordinal);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "approval-response-2",
                AgentMessageTypes.ApprovalResponse,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var replay = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("approval_stale", replay.Payload["error_code"]!.GetValue<string>());
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

        public static async Task<HostFixture> StartAsync(IVolumeController? volumeController = null)
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

            var host = volumeController is null
                ? await BrokerHost.StartAsync(descriptorPath)
                : await BrokerHost.StartAsync(descriptorPath, volumeController);
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

    private sealed class FakeVolumeController(int level, bool muted = false) : IVolumeController
    {
        public VolumeState State { get; private set; } = new(level, muted);
        public List<(string Kind, int Value)> Writes { get; } = [];

        public VolumeState GetState() => State;

        public void SetLevel(int value)
        {
            Writes.Add(("level", value));
            State = State with { Level = value };
        }

        public void SetMuted(bool value) => State = State with { Muted = value };
    }

    private static async Task SendAsync(ClientWebSocket socket, AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<AgentEnvelope> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}
