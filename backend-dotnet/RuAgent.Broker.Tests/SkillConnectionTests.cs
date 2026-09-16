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
    /// <summary>
    /// Deadline for the websocket round trips these tests make. It guards against a
    /// hang, not a performance budget: at five seconds it expired often enough to fail
    /// roughly one run in four while the rest of the suite ran in parallel, and the
    /// failure surfaced as a bare assertion with no frame to show.
    /// </summary>
    private static readonly TimeSpan ReceiveDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Skill_start_emits_bounded_events_and_one_result()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(ReceiveDeadline);
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);

        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject { ["profile"] = "agent" }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute),
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
        // The Broker emits one SkillEvent per executed step and exactly one
        // SkillResult. Asserting "one event then the result" only held while this
        // skill happened to execute a single step; drain until the result instead.
        //
        // The deadline guarding these receives is a hang guard, not a budget: it was
        // five seconds, which is under what a dozen websocket round trips take while
        // the rest of the suite runs in parallel, and it expired often enough to fail
        // roughly one run in four on a loaded machine.
        var steps = new List<AgentEnvelope>();
        AgentEnvelope resultEnvelope;
        while (true)
        {
            var next = await ReceiveAsync(socket, cancellation.Token);
            if (next.MessageType == AgentMessageTypes.SkillResult)
            {
                resultEnvelope = next;
                break;
            }

            // Name the frame that arrived instead of failing on a bare mismatch: this
            // loop is the only place a wrong message type would surface.
            Assert.True(
                next.MessageType == AgentMessageTypes.SkillEvent,
                $"Expected a SkillEvent or the SkillResult, received {next.MessageType}.");

            // A step event never carries the skill's output payload.
            Assert.DoesNotContain("output", next.Payload);
            Assert.NotNull(next.Payload["step_index"]);
            steps.Add(next);
        }

        Assert.Equal(AgentMessageTypes.SkillResult, resultEnvelope.MessageType);

        // Report the Broker's own answer when the run did not succeed instead of
        // failing on a bare status mismatch: a cancelled run (the receive deadline
        // firing on a loaded machine) and a genuinely failed skill need different
        // follow-up, and the error code is what tells them apart.
        string status = resultEnvelope.Payload["status"]!.GetValue<string>();
        string? errorCode = resultEnvelope.Payload["error_code"]?.GetValue<string>();
        int reportedSteps = resultEnvelope.Payload["step_count"]!.GetValue<int>();

        Assert.True(
            string.Equals(status, ToolResultStatuses.Succeeded, StringComparison.Ordinal),
            $"Skill did not succeed: status={status} error_code={errorCode ?? "(none)"} "
                + $"state={resultEnvelope.Payload["state"]?.GetValue<string>()} steps={reportedSteps} "
                + $"summary={resultEnvelope.Payload["safe_summary"]?.GetValue<string>()}");

        Assert.NotNull(resultEnvelope.Payload["receipt_id"]);

        // The result reports exactly the steps the stream carried, and a successful
        // run always carried at least one.
        Assert.Equal(steps.Count, reportedSteps);
        Assert.NotEmpty(steps);
    }
    [Fact]
    public async Task Skill_start_duplicate_request_id_conflicts_when_input_changes()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(ReceiveDeadline);
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);
        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute),
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
        using var cancellation = new CancellationTokenSource(ReceiveDeadline);
        await socket.ConnectAsync(fixture.WebSocketUri, cancellation.Token);

        await SendAsync(socket, Envelope("hello", AgentMessageTypes.Hello, new JsonObject { ["profile"] = "agent" }), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(socket, Envelope("open", AgentMessageTypes.SessionOpen, new JsonObject
        {
            ["scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute),
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
        using var cancellation = new CancellationTokenSource(ReceiveDeadline);
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
                "http://127.0.0.1:" + GetFreePort(), token, 1, sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5));            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            var skill = new SkillManifest(
                "workspace.list.skill", "1.0.0",
                new JsonObject { ["type"] = "object" },
                new JsonObject { ["type"] = "object" },
                new HashSet<string>(["read"]),
                new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute]),
                [new SkillStep(SkillStepKind.CallTool, "list", "read", new JsonObject { ["op"] = "list", ["path"] = ".", ["max_entries"] = 8 })],
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
        // A websocket message can arrive in several frames, and ReceiveAsync returns one
        // frame at a time. Parsing the first frame alone reads whatever bytes happened to
        // land together, which is truncated JSON whenever the split falls mid message —
        // the source of the intermittent failure this helper used to cause.
        var buffer = new byte[64 * 1024];
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The broker closed the socket before replying.");
            }

            payload.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length));
    }
}
