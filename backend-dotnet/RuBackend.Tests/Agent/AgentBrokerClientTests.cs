using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RuAgent.Protocol;
using RuBackend.Agent;
using Xunit;

namespace RuBackend.Tests.Agent;

public sealed class AgentBrokerClientTests
{
    [Fact]
    public async Task Client_parses_capabilities_actual_grant_and_verified_receipt()
    {
        await using var fixture = await ClientFixture.StartAsync();
        await using var client = fixture.Client;

        var capabilities = await client.GetCapabilitiesAsync(
            CapabilityProfile.Agent,
            CancellationToken.None);
        var fileWrite = Assert.Single(capabilities);
        Assert.Equal("files.write", fileWrite.ToolId);
        Assert.Equal("1.0.0", fileWrite.ToolVersion);
        Assert.Equal(new[] { "workspace.write" }, fileWrite.Scopes);
        Assert.True(fileWrite.ModelEnabled);

        var grant = await client.OpenSessionAsync(
            "session-1",
            new HashSet<string>(StringComparer.Ordinal) { "workspace.write", "workspace.read" },
            new HashSet<string>(StringComparer.Ordinal),
            "corr-1",
            2_000,
            CancellationToken.None);
        Assert.Equal(new[] { "workspace.write" }, grant.Scopes);
        Assert.Empty(grant.SkillIds);

        var result = await client.ExecuteToolAsync(
            new AgentToolCall(
                "request-1",
                "session-1",
                "corr-1",
                "files.write",
                "1.0.0",
                "tool",
                new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" },
                2_000),
            CapabilityProfile.Agent,
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal("verified", result.VerificationState);
        Assert.Equal("default_render_multimedia", result.EndpointRole);
        Assert.Matches("^[0-9a-f]{16}$", result.EndpointHash);
        Assert.Equal(12, result.DurationMs);
        Assert.True(result.AdapterInvoked);
        var request = Assert.Single(fixture.Server.Requests, item => item.MessageType == AgentMessageTypes.ToolRequest);
        Assert.Equal("tool", request.Payload["capability_class"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_rejects_an_unknown_exact_binding_without_a_tool_request()
    {
        await using var fixture = await ClientFixture.StartAsync();
        await using var client = fixture.Client;
        _ = await client.GetCapabilitiesAsync(CapabilityProfile.Agent, CancellationToken.None);
        _ = await client.OpenSessionAsync(
            "session-1",
            new HashSet<string>(["workspace.write"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            "corr-1",
            2_000,
            CancellationToken.None);
        var before = fixture.Server.Requests.Count;

        var result = await client.ExecuteToolAsync(
            new AgentToolCall(
                "request-missing",
                "session-1",
                "corr-1",
                "files.write",
                "2.0.0",
                "tool",
                new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" },
                2_000),
            CapabilityProfile.Agent,
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("capability_unavailable", result.ErrorCode);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(before, fixture.Server.Requests.Count);
    }

    [Fact]
    public async Task Client_requires_every_scope_from_the_actual_grant()
    {
        await using var fixture = await ClientFixture.StartAsync(
            new FakeBrokerOptions { SessionScopes = new JsonArray() });
        await using var client = fixture.Client;
        _ = await client.OpenSessionAsync(
            "session-1",
            new HashSet<string>(["workspace.write"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            "corr-1",
            2_000,
            CancellationToken.None);
        var before = fixture.Server.Requests.Count;

        var result = await client.ExecuteToolAsync(
            new AgentToolCall(
                "request-scope",
                "session-1",
                "corr-1",
                "files.write",
                "1.0.0",
                "tool",
                new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" },
                2_000),
            CapabilityProfile.Agent,
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("scope_not_granted", result.ErrorCode);
        Assert.False(result.AdapterInvoked);
        Assert.Equal(before, fixture.Server.Requests.Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("risk")]
    [InlineData("availability")]
    public async Task Client_rejects_malformed_capability_snapshots(string mutation)
    {
        var tools = DefaultTools();
        switch (mutation)
        {
            case "duplicate":
                tools.Add(tools[0]!.DeepClone());
                break;
            case "risk":
                tools[0]!["risk"] = "unknown";
                break;
            case "availability":
                tools[0]!["availability"] = "imaginary";
                break;
        }

        await using var fixture = await ClientFixture.StartAsync(
            new FakeBrokerOptions { Tools = tools });
        await using var client = fixture.Client;

        var error = await Assert.ThrowsAsync<AgentBrokerException>(() =>
            client.GetCapabilitiesAsync(CapabilityProfile.Agent, CancellationToken.None));

        Assert.Equal("invalid_capabilities", error.Code);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("endpoint_hash")]
    [InlineData("provenance")]
    public async Task Client_rejects_malformed_receipt_proof(string mutation)
    {
        var receipt = DefaultToolResult();
        switch (mutation)
        {
            case "duration":
                receipt["duration_ms"] = -1;
                break;
            case "endpoint_hash":
                receipt["endpoint_hash"] = "ABCDEF0123456789";
                break;
            case "provenance":
                receipt.Remove("adapter_invoked");
                break;
        }

        await using var fixture = await ClientFixture.StartAsync(
            new FakeBrokerOptions { ToolResult = receipt });
        await using var client = fixture.Client;
        _ = await client.OpenSessionAsync(
            "session-1",
            new HashSet<string>(["workspace.write"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            "corr-1",
            2_000,
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentBrokerException>(() =>
            client.ExecuteToolAsync(
                new AgentToolCall(
                    "request-invalid",
                    "session-1",
                    "corr-1",
                    "files.write",
                    "1.0.0",
                    "tool",
                    new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "hello" },
                    2_000),
                CapabilityProfile.Agent,
                CancellationToken.None));

        Assert.Equal("invalid_tool_result", error.Code);
    }

    [Fact]
    public async Task Client_sends_approval_decision_without_raw_tool_arguments()
    {
        await using var fixture = await ClientFixture.StartAsync();
        await using var client = fixture.Client;

        var result = await client.RespondToApprovalAsync(
            new AgentApprovalResponse("approval-1", "session-1", true),
            "corr-approval",
            10_000,
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        var approval = Assert.Single(fixture.Server.Requests, request => request.MessageType == AgentMessageTypes.ApprovalResponse);
        Assert.Equal("approval-1", approval.Payload["approval_id"]!.GetValue<string>());
        Assert.True(approval.Payload["approved"]!.GetValue<bool>());
        Assert.DoesNotContain("arguments", approval.Payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", approval.Payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Client_source_contains_the_bounded_skill_execution_path()
    {
        var source = File.ReadAllText(FindSource("AgentBrokerClient.cs"));

        Assert.Contains("ListSkillsAsync", source, StringComparison.Ordinal);
        Assert.Contains("SkillRoundTripAsync", source, StringComparison.Ordinal);
        Assert.Contains("AgentMessageTypes.SkillStart", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_rejects_an_oversized_descriptor_before_parsing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-agent-descriptor-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "broker.json");
            File.WriteAllText(path, new string('x', 1 * 1024 * 1024 + 1));

            var error = Assert.Throws<AgentBrokerException>(() => new AgentBrokerClient(path));

            Assert.Equal("invalid_descriptor", error.Code);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static JsonArray DefaultTools() => new()
    {
        new JsonObject
        {
            ["tool_id"] = "files.write",
            ["tool_version"] = "1.0.0",
            ["scopes"] = new JsonArray("workspace.write"),
            ["risk"] = "reversible",
            ["availability"] = "executable",
            ["model_enabled"] = true,
        },
    };

    private static JsonObject DefaultToolResult() => new()
    {
        ["status"] = ToolResultStatuses.Succeeded,
        ["output"] = new JsonObject { ["path"] = "notes.txt" },
        ["error_code"] = null,
        ["safe_summary"] = "Workspace file written.",
        ["duration_ms"] = 12,
        ["verification_state"] = "verified",
        ["endpoint_role"] = "default_render_multimedia",
        ["endpoint_hash"] = "0123456789abcdef",
        ["adapter_invoked"] = true,
    };

    private static string FindSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "backend-dotnet", "RuBackend", "Agent", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(fileName);
    }

    private sealed class ClientFixture : IAsyncDisposable
    {
        private ClientFixture(string root, FakeBrokerServer server, AgentBrokerClient client)
        {
            Root = root;
            Server = server;
            Client = client;
        }

        public string Root { get; }
        public FakeBrokerServer Server { get; }
        public AgentBrokerClient Client { get; }

        public static async Task<ClientFixture> StartAsync(FakeBrokerOptions? options = null)
        {
            var server = await FakeBrokerServer.StartAsync(options ?? new FakeBrokerOptions());
            var root = Path.Combine(Path.GetTempPath(), "ru-agent-client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(
                    new BrokerSessionDescriptor(
                        server.BaseUrl,
                        "secret-token",
                        1,
                        sessionRoot,
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    BrokerSessionDescriptor.JsonOptions));
            return new ClientFixture(root, server, new AgentBrokerClient(descriptorPath));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record FakeBrokerOptions
    {
        public JsonArray Tools { get; init; } = DefaultTools();
        public JsonArray SessionScopes { get; init; } = new("workspace.write");
        public JsonObject ToolResult { get; init; } = DefaultToolResult();
    }

    private sealed class FakeBrokerServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly List<AgentEnvelope> _requests;

        private FakeBrokerServer(WebApplication application, string baseUrl, List<AgentEnvelope> requests)
        {
            _application = application;
            BaseUrl = baseUrl;
            _requests = requests;
        }

        public string BaseUrl { get; }
        public IReadOnlyList<AgentEnvelope> Requests => _requests;

        public static async Task<FakeBrokerServer> StartAsync(FakeBrokerOptions options)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            application.UseWebSockets();
            var requests = new List<AgentEnvelope>();
            application.Map("/agent", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var hello = await ReceiveAsync(socket);
                await SendAsync(
                    socket,
                    new AgentEnvelope(
                        1,
                        hello.RequestId,
                        AgentMessageTypes.HelloOk,
                        hello.SessionId,
                        hello.CorrelationId,
                        hello.DeadlineMs,
                        new JsonObject
                        {
                            ["protocol_version"] = 1,
                            ["tools"] = options.Tools.DeepClone(),
                            ["skills"] = new JsonArray(),
                        }));
                var request = await ReceiveAsync(socket);
                lock (requests)
                {
                    requests.Add(request);
                }

                var (messageType, payload) = request.MessageType switch
                {
                    AgentMessageTypes.Health => (
                        AgentMessageTypes.HealthOk,
                        new JsonObject { ["status"] = "ready" }),
                    AgentMessageTypes.SessionOpen => (
                        AgentMessageTypes.SessionOpened,
                        new JsonObject
                        {
                            ["status"] = ToolResultStatuses.Succeeded,
                            ["scopes"] = options.SessionScopes.DeepClone(),
                            ["skill_ids"] = new JsonArray(),
                            ["safe_summary"] = "session opened",
                        }),
                    AgentMessageTypes.SessionClose => (
                        AgentMessageTypes.SkillResult,
                        new JsonObject { ["status"] = ToolResultStatuses.Succeeded }),
                    _ => (AgentMessageTypes.ToolResult, options.ToolResult.DeepClone().AsObject()),
                };
                await SendAsync(
                    socket,
                    new AgentEnvelope(
                        1,
                        request.RequestId,
                        messageType,
                        request.SessionId,
                        request.CorrelationId,
                        request.DeadlineMs,
                        payload));
            });
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            return new FakeBrokerServer(application, addresses.Single().TrimEnd('/'), requests);
        }

        public async ValueTask DisposeAsync() => await _application.DisposeAsync();

        private static async Task<AgentEnvelope> ReceiveAsync(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        private static Task SendAsync(WebSocket socket, AgentEnvelope envelope)
        {
            var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
            return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
