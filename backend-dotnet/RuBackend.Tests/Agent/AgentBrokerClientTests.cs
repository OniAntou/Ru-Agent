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
    public async Task Client_proves_descriptor_protocol_and_session_before_tool_call()
    {
        await using var server = await FakeBrokerServer.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "ru-agent-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(
                    new BrokerSessionDescriptor(
                        server.BaseUrl,
                        "secret-token",
                        1,
                        Path.Combine(root, "session"),
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    BrokerSessionDescriptor.JsonOptions));

            await using var client = new AgentBrokerClient(descriptorPath);
            var grant = await client.OpenSessionAsync(
                "session-1",
                new HashSet<string>(["workspace.read"], StringComparer.Ordinal),
                new HashSet<string>(["skill-1"], StringComparer.Ordinal),
                "corr-1",
                10_000,
                CancellationToken.None);

            Assert.Equal("session-1", grant.SessionId);
            var result = await client.ExecuteToolAsync(
                "session-1",
                "files.list",
                "1.0.0",
                new JsonObject { ["path"] = "src" },
                "workspace.read",
                "corr-1",
                10_000,
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Equal("src", result.Output["path"]!.GetValue<string>());
            Assert.Contains(
                server.Requests,
                request => request.MessageType == AgentMessageTypes.ToolRequest
                    && request.SessionId == "session-1"
                    && request.CorrelationId == "corr-1");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Client_rejects_tool_without_granted_scope_without_contacting_broker()
    {
        await using var server = await FakeBrokerServer.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "ru-agent-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(
                    new BrokerSessionDescriptor(
                        server.BaseUrl,
                        "secret-token",
                        1,
                        Path.Combine(root, "session"),
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    BrokerSessionDescriptor.JsonOptions));

            await using var client = new AgentBrokerClient(descriptorPath);
            await client.OpenSessionAsync(
                "session-1",
                new HashSet<string>(["workspace.read"], StringComparer.Ordinal),
                new HashSet<string>(),
                "corr-1",
                10_000,
                CancellationToken.None);
            var before = server.Requests.Count;

            var result = await client.ExecuteToolAsync(
                "session-1",
                "files.write",
                "1.0.0",
                new JsonObject { ["path"] = "out.txt" },
                "workspace.write",
                "corr-1",
                10_000,
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Denied, result.Status);
            Assert.Equal(before, server.Requests.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Client_sends_approval_decision_without_raw_tool_arguments()
    {
        await using var server = await FakeBrokerServer.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "ru-agent-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(
                    new BrokerSessionDescriptor(
                        server.BaseUrl,
                        "secret-token",
                        1,
                        Path.Combine(root, "session"),
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    BrokerSessionDescriptor.JsonOptions));

            await using var client = new AgentBrokerClient(descriptorPath);
            var result = await client.RespondToApprovalAsync(
                new AgentApprovalResponse("approval-1", "session-1", true),
                "corr-approval",
                10_000,
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            var approval = Assert.Single(server.Requests, request => request.MessageType == AgentMessageTypes.ApprovalResponse);
            Assert.Equal("approval-1", approval.Payload["approval_id"]!.GetValue<string>());
            Assert.True(approval.Payload["approved"]!.GetValue<bool>());
            Assert.DoesNotContain("arguments", approval.Payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("script", approval.Payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeBrokerServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly List<AgentEnvelope> _requests = [];

        private FakeBrokerServer(WebApplication application, string baseUrl, List<AgentEnvelope> requests)
        {
            _application = application;
            BaseUrl = baseUrl;
            _requests = requests;
        }

        public string BaseUrl { get; }
        public IReadOnlyList<AgentEnvelope> Requests => _requests;

        public static async Task<FakeBrokerServer> StartAsync()
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
                        new JsonObject { ["protocol_version"] = 1 }));
                var request = await ReceiveAsync(socket);
                lock (requests)
                {
                    requests.Add(request);
                }

                var output = request.MessageType == AgentMessageTypes.ToolRequest
                    ? new JsonObject { ["path"] = request.Payload["arguments"]?["path"]?.GetValue<string>() }
                    : new JsonObject();
                await SendAsync(
                    socket,
                    new AgentEnvelope(
                        1,
                        request.RequestId,
                        request.MessageType == AgentMessageTypes.SessionOpen
                            ? AgentMessageTypes.SkillResult
                            : AgentMessageTypes.ToolResult,
                        request.SessionId,
                        request.CorrelationId,
                        request.DeadlineMs,
                        new JsonObject
                        {
                            ["status"] = ToolResultStatuses.Succeeded,
                            ["output"] = output,
                            ["safe_summary"] = "Fake broker completed the request.",
                        }));
            });
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var baseUrl = addresses.Single().TrimEnd('/');
            return new FakeBrokerServer(application, baseUrl, requests);
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
