using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillConnectionTests
{
    [Fact]
    public async Task Skill_start_emits_bounded_events_and_one_result()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);

        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject { ["profile"] = "agent" }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SkillExecute),
            ["skill_ids"] = new JsonArray("workspace.list.skill"),
        }), cancellation.Token);
        var opened = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal(AgentMessageTypes.SessionOpened, opened.MessageType);
        Assert.Equal("workspace.list.skill", opened.Payload["skill_ids"]![0]!.GetValue<string>());

        await SendAsync(socket, Envelope("start", AgentMessageTypes.SkillStart, new JsonObject
        {
            ["skill_id"] = "workspace.list.skill",
            ["version"] = "1.0.0",
            ["input"] = new JsonObject(),
        }), cancellation.Token);
        var eventEnvelope = await ReceiveAsync(socket, cancellation.Token);
        var resultEnvelope = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.SkillEvent, eventEnvelope.MessageType);
        Assert.DoesNotContain("output", eventEnvelope.Payload);
        Assert.Equal(AgentMessageTypes.SkillResult, resultEnvelope.MessageType);
        Assert.Equal(ToolResultStatuses.Succeeded, resultEnvelope.Payload["status"]!.GetValue<string>());
        Assert.NotNull(resultEnvelope.Payload["receipt_id"]);
    }

    [Fact]
    public async Task Skill_start_duplicate_request_id_conflicts_when_input_changes()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);
        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SkillExecute),
            ["skill_ids"] = new JsonArray("workspace.list.skill"),
        }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(socket, Envelope("same", AgentMessageTypes.SkillStart, new JsonObject
        {
            ["skill_id"] = "workspace.list.skill", ["version"] = "1.0.0", ["input"] = new JsonObject(),
        }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("same", AgentMessageTypes.SkillStart, new JsonObject
        {
            ["skill_id"] = "workspace.list.skill", ["version"] = "1.0.0", ["input"] = new JsonObject { ["different"] = true },
        }), cancellation.Token);
        var conflict = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.SkillResult, conflict.MessageType);
        Assert.Equal("request_conflict", conflict.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Skill_start_failure_uses_the_same_bounded_result_shape()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);

        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject { ["profile"] = "agent" }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SkillExecute),
            ["skill_ids"] = new JsonArray("workspace.list.skill"),
        }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(socket, Envelope("bad-input", AgentMessageTypes.SkillStart, new JsonObject
        {
            ["skill_id"] = "workspace.list.skill",
            ["version"] = "1.0.0",
        }), cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.SkillResult, result.MessageType);
        Assert.Equal("schema_invalid", result.Payload["error_code"]!.GetValue<string>());
        Assert.Equal("workspace.list.skill", result.Payload["skill_id"]!.GetValue<string>());
        Assert.Equal("1.0.0", result.Payload["version"]!.GetValue<string>());
        Assert.Equal("Failed", result.Payload["state"]!.GetValue<string>());
        Assert.Equal(0, result.Payload["step_count"]!.GetValue<int>());
        Assert.IsType<JsonObject>(result.Payload["output"]);
    }

    [Fact]
    public async Task Skill_start_without_an_open_session_uses_the_same_bounded_result_shape()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);

        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("not-open", AgentMessageTypes.SkillStart, new JsonObject
        {
            ["skill_id"] = "workspace.list.skill",
            ["version"] = "1.0.0",
            ["input"] = new JsonObject(),
        }), cancellation.Token);

        var result = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal(AgentMessageTypes.SkillResult, result.MessageType);
        Assert.Equal(ToolResultStatuses.Denied, result.Payload["status"]!.GetValue<string>());
        Assert.Equal("broker_not_ready", result.Payload["error_code"]!.GetValue<string>());
        Assert.Equal("workspace.list.skill", result.Payload["skill_id"]!.GetValue<string>());
        Assert.Equal("1.0.0", result.Payload["version"]!.GetValue<string>());
        Assert.Equal("Failed", result.Payload["state"]!.GetValue<string>());
        Assert.IsType<JsonObject>(result.Payload["output"]);
    }

    private static AgentEnvelope Envelope(string requestId, string type, JsonObject payload) =>
        new(1, requestId, type, "skill-session", null, 10_000, payload);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, string token, BrokerHost host)
        {
            Root = root;
            Token = token;
            Host = host;
        }

        public string Root { get; }
        public string Token { get; }
        public BrokerHost Host { get; }
        public Uri WebSocketUri => new(Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent");

        public static async Task<Fixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-skill-connection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var token = "token-" + Guid.NewGuid().ToString("N");
            var descriptorPath = Path.Combine(root, "broker.json");
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(), token, 1, sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5));
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            var skill = new SkillManifest(
                "workspace.list.skill", "1.0.0",
                new JsonObject { ["type"] = "object" },
                new JsonObject { ["type"] = "object" },
                new HashSet<string>(["files.list"]),
                new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SkillExecute]),
                [new SkillStep(SkillStepKind.CallTool, "list", "files.list", new JsonObject { ["path"] = ".", ["max_entries"] = 8 })],
                false, 4, 30_000, "builtin", "Data only.");
            var host = await BrokerHost.StartAsync(new BrokerOptions(descriptorPath, BuiltinSkills: [skill]));
            return new Fixture(root, token, host);
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
