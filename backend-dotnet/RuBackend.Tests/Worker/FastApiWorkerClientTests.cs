using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using RuBackend.Compatibility;
using RuBackend.Configuration;
using RuBackend.Tests.Fakes;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class FastApiWorkerClientTests
{
    [Fact]
    public async Task GenerateChat_maps_legacy_frames_and_preserves_token_auth()
    {
        await using var sockets = await WebSocketPair.StartAsync();
        await using var client = CreateClient(
            sockets.Connector,
            HealthyTransport(),
            "desktop secret");
        var request = new WorkerChatRequest(
            "chat-legacy",
            "xin chào",
            "session-1",
            [],
            string.Empty,
            string.Empty,
            []);
        var upstream = Task.Run(async () =>
        {
            var sent = JsonNode.Parse(await sockets.Peer.ReceiveTextAsync())!.AsObject();
            await sockets.Peer.SendTextAsync("{\"type\":\"reset\"}");
            await sockets.Peer.SendTextAsync("{\"type\":\"chunk\",\"content\":\"Em nghe \"}");
            await sockets.Peer.SendTextAsync(
                "{\"type\":\"done\",\"content\":\"Em nghe đây.\",\"session_id\":\"session-1\"}");
            return sent;
        });

        var events = await CollectAsync(client.GenerateChatAsync(request, CancellationToken.None));
        var observed = await upstream;

        Assert.Equal("xin chào", observed["prompt"]!.GetValue<string>());
        Assert.Equal("session-1", observed["session_id"]!.GetValue<string>());
        Assert.Equal(
            [WorkerMessageTypes.ChatReset, WorkerMessageTypes.ChatChunk, WorkerMessageTypes.ChatDone],
            events.Select(item => item.Type));
        Assert.Equal([0L, 1L, 2L], events.Select(item => item.Sequence));
        Assert.Equal("Em nghe ", events[1].Payload["text"]!.GetValue<string>());
        Assert.Equal("Em nghe đây.", events[2].Payload["raw_reply"]!.GetValue<string>());
        Assert.Equal("/chat", sockets.Connector.LastRequestedUri!.AbsolutePath);
        Assert.Equal("?token=desktop%20secret", sockets.Connector.LastRequestedUri.Query);
    }

    [Fact]
    public async Task GetHealth_maps_status_and_uses_bearer_auth()
    {
        var transport = HealthyTransport();
        await using var client = CreateClient(
            new NeverUsedWebSocketConnector(),
            transport,
            "worker-secret");

        var health = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(health.Ready);
        Assert.Equal(1, health.Version);
        Assert.Contains("chat", health.Capabilities);
        Assert.Equal("/status", transport.LastRequest!.PathAndQuery);
        Assert.Equal("Bearer worker-secret", transport.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task ExtractFacts_is_deterministically_unsupported_in_rollback_mode()
    {
        var transport = HealthyTransport();
        await using var client = CreateClient(new NeverUsedWebSocketConnector(), transport);

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            client.ExtractFactsAsync(
                "memory-legacy",
                "prompt",
                "response",
                CancellationToken.None));

        Assert.Equal("unsupported_operation", error.Code);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task GetVtsCatalog_is_unavailable_without_calling_a_public_endpoint()
    {
        var transport = HealthyTransport();
        await using var client = CreateClient(new NeverUsedWebSocketConnector(), transport);

        var catalog = await client.GetVtsCatalogAsync(
            "legacy-vts-catalog",
            CancellationToken.None);

        Assert.Null(catalog);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task TriggerVtsActions_maps_codes_to_the_legacy_private_action_call()
    {
        var transport = new StubUpstreamHttpTransport(new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            "{\"status\":\"ok\"}"u8.ToArray()));
        await using var client = CreateClient(
            new NeverUsedWebSocketConnector(),
            transport,
            "desktop-secret");

        await client.TriggerVtsActionsAsync(
            "legacy-vts-action",
            ["vts_01", "expr_01"],
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, transport.LastRequest!.Method);
        Assert.Equal("/vts/action", transport.LastRequest.PathAndQuery);
        Assert.Equal("Bearer desktop-secret", transport.LastRequest.Headers["Authorization"]);
        var body = JsonNode.Parse(transport.LastRequest.Body)!.AsObject();
        Assert.Equal("vts_01 expr_01", body["codes"]!.GetValue<string>());
    }

    [Fact]
    public async Task Health_503_maps_to_redacted_worker_unavailable()
    {
        var transport = new StubUpstreamHttpTransport(new UpstreamResponse(
            StatusCodes.Status503ServiceUnavailable,
            "application/json",
            "{\"detail\":\"private model path and secret\"}"u8.ToArray()));
        await using var client = CreateClient(new NeverUsedWebSocketConnector(), transport);

        var error = await Assert.ThrowsAsync<WorkerUnavailableException>(() =>
            client.GetHealthAsync(CancellationToken.None));

        Assert.DoesNotContain("private", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateChat_rejects_malformed_legacy_JSON()
    {
        await using var sockets = await WebSocketPair.StartAsync();
        await using var client = CreateClient(sockets.Connector, HealthyTransport());
        var upstream = Task.Run(async () =>
        {
            await sockets.Peer.ReceiveTextAsync();
            await sockets.Peer.SendTextAsync("{");
        });

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await CollectAsync(client.GenerateChatAsync(
                EmptyChatRequest("chat-malformed"),
                CancellationToken.None)));

        await upstream;
        Assert.Equal("invalid_worker_response", error.Code);
    }

    [Fact]
    public async Task Cancelling_chat_closes_the_legacy_socket()
    {
        await using var sockets = await WebSocketPair.StartAsync();
        await using var client = CreateClient(sockets.Connector, HealthyTransport());
        using var cancellation = new CancellationTokenSource();
        var collect = CollectAsync(client.GenerateChatAsync(
            EmptyChatRequest("chat-cancel-legacy"),
            cancellation.Token));
        await sockets.Peer.ReceiveTextAsync();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collect);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await sockets.Peer.ReceiveCloseAsync());
    }

    private static FastApiWorkerClient CreateClient(
        IUpstreamWebSocketConnector connector,
        StubUpstreamHttpTransport transport,
        string token = "worker-secret") =>
        new(
            transport,
            connector,
            Options.Create(new BackendOptions
            {
                DesktopToken = token,
                UpstreamUrl = "http://127.0.0.1:8000",
            }));

    private static StubUpstreamHttpTransport HealthyTransport() =>
        new(new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            "{\"status\":\"ok\"}"u8.ToArray()));

    private static WorkerChatRequest EmptyChatRequest(string requestId) =>
        new(requestId, "prompt", "session", [], string.Empty, string.Empty, []);

    private static async Task<List<WorkerEvent>> CollectAsync(
        IAsyncEnumerable<WorkerEvent> events)
    {
        var result = new List<WorkerEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class NeverUsedWebSocketConnector : IUpstreamWebSocketConnector
    {
        public Task<WebSocket> ConnectAsync(Uri upstreamUri, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("WebSocket transport was not expected.");
    }

    private sealed class WebSocketPair : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private WebSocketPair(
            WebApplication application,
            StubUpstreamWebSocketConnector connector,
            TestWebSocketPeer peer)
        {
            _application = application;
            Connector = connector;
            Peer = peer;
        }

        public StubUpstreamWebSocketConnector Connector { get; }

        public TestWebSocketPeer Peer { get; }

        public static async Task<WebSocketPair> StartAsync()
        {
            var accepted = new TaskCompletionSource<WebSocket>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var application = builder.Build();
            application.UseWebSockets();
            application.Run(async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                accepted.TrySetResult(socket);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
            });
            await application.StartAsync();
            var client = await application.GetTestServer()
                .CreateWebSocketClient()
                .ConnectAsync(new Uri("ws://localhost/upstream"), CancellationToken.None);
            var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new WebSocketPair(
                application,
                new StubUpstreamWebSocketConnector(server),
                new TestWebSocketPeer(client));
        }

        public async ValueTask DisposeAsync()
        {
            Peer.Dispose();
            await _application.DisposeAsync();
        }
    }
}
