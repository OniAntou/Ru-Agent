using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerV1ServerContractTests
{
    [Fact]
    public async Task Server_authenticates_hello_and_emits_health_response()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "/worker/v1");
        await SendAsync(socket, Envelope("hello-1", WorkerV1Contracts.Hello, new JsonObject
        {
            ["token"] = "worker-secret",
        }));

        var hello = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(socket));
        Assert.Equal(WorkerV1Contracts.HelloOk, hello.Type);
        Assert.Equal(0, hello.Sequence);
        Assert.Contains("runtime", hello.Payload["capabilities"]!.AsArray().Select(node => node!.GetValue<string>()));

        await SendAsync(socket, Envelope("health-1", WorkerV1Contracts.WorkerHealth, new JsonObject()));
        var health = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(socket));
        Assert.Equal(WorkerV1Contracts.WorkerHealthOk, health.Type);
        Assert.Equal(0, health.Sequence);
        Assert.True(health.Payload["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Server_routes_a_request_and_assigns_ordered_sequence()
    {
        await using var host = await StartHostAsync(new FakeRouter(
            new WorkerV1Reply(
                WorkerV1Contracts.VoiceDone,
                new JsonObject { ["state"] = "done" })));
        using var socket = await ConnectAsync(host, "/worker/v1");
        await SendAsync(socket, Envelope("hello-1", WorkerV1Contracts.Hello, new JsonObject
        {
            ["token"] = "worker-secret",
        }));
        _ = await ReceiveAsync(socket);

        await SendAsync(socket, Envelope("request-1", WorkerV1Contracts.VoiceStart, new JsonObject
        {
            ["operation"] = "tts",
        }));

        var response = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(socket));
        Assert.Equal(WorkerV1Contracts.VoiceDone, response.Type);
        Assert.Equal("request-1", response.Id);
        Assert.Equal(0, response.Sequence);
        Assert.Equal("done", response.Payload["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_closes_wrong_token_without_an_error_payload()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "/worker/v1");
        await SendAsync(socket, Envelope("hello-1", WorkerV1Contracts.Hello, new JsonObject
        {
            ["token"] = "wrong-token",
        }));

        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    public async Task Server_maps_binary_control_frames_to_bounded_error()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "/worker/v1");
        await SendAsync(socket, Envelope("hello-1", WorkerV1Contracts.Hello, new JsonObject
        {
            ["token"] = "worker-secret",
        }));
        _ = await ReceiveAsync(socket);

        await socket.SendAsync(new ArraySegment<byte>([1, 2, 3]), WebSocketMessageType.Binary, true, CancellationToken.None);

        var error = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(socket));
        Assert.Equal(WorkerV1Contracts.Error, error.Type);
        Assert.Equal("connection", error.Id);
        Assert.Equal("text_control_required", error.Payload["code"]!.GetValue<string>());
    }

    private static async Task<WebApplication> StartHostAsync(IWorkerV1RequestRouter? router = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(new WorkerV1Server("worker-secret", router ?? new FakeRouter()).HandleAsync);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebSocket> ConnectAsync(WebApplication host, string path) =>
        await host.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);

    private static string Envelope(string id, string type, JsonObject payload) =>
        WorkerV1EnvelopeCodec.Serialize(new WorkerV1Envelope(
            WorkerV1Contracts.ProtocolVersion,
            id,
            type,
            null,
            payload));

    private static async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[128 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("socket closed before a text response");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private sealed class FakeRouter(params WorkerV1Reply[] replies) : IWorkerV1RequestRouter
    {
        public async IAsyncEnumerable<WorkerV1Reply> RouteAsync(
            string type,
            JsonObject payload,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var reply in replies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return reply;
            }
        }
    }
}
