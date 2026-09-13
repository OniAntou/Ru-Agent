using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class LoopbackWorkerClientTests
{
    [Fact]
    public async Task Connect_sends_the_token_and_returns_agent_capabilities()
    {
        string? observedToken = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            observedToken = hello.Payload["token"]!.GetValue<string>();
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "runtime", "voice"));
        });
        await using var client = CreateClient(server.Uri, "worker-secret");

        var hello = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("worker-secret", observedToken);
        Assert.Equal(1, hello.Version);
        Assert.Equal(
            new HashSet<string> { "runtime", "voice" },
            hello.Capabilities);
    }

    [Fact]
    public async Task InvokeRuntime_preserves_the_private_route_contract()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "runtime"));
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                observed.Id,
                WorkerMessageTypes.RuntimeResponse,
                0,
                new JsonObject
                {
                    ["status_code"] = 200,
                    ["content_type"] = "application/json",
                    ["headers"] = new JsonObject { ["x-runtime"] = "worker" },
                    ["body"] = "{\"status\":\"ok\"}",
                }));
        });
        await using var client = CreateClient(server.Uri);

        var response = await client.InvokeRuntimeAsync(
            new WorkerRuntimeRequest(
                "runtime-1",
                "POST",
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes("{\"text\":\"Xin chào\"}")),
            CancellationToken.None);

        Assert.Equal(WorkerMessageTypes.RuntimeRequest, observed!.Type);
        Assert.Equal("POST", observed.Payload["method"]!.GetValue<string>());
        Assert.Equal(
            "{\"text\":\"Xin chào\"}",
            observed.Payload["body"]!.GetValue<string>());
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("worker", response.Headers!["x-runtime"]);
    }

    [Fact]
    public async Task InvokeRuntime_maps_binary_responses_without_decoding_them()
    {
        var binary = Encoding.UTF8.GetBytes("{\"audio\":true}");
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "runtime"));
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.RuntimeResponse,
                0,
                new JsonObject
                {
                    ["status_code"] = 200,
                    ["content_type"] = "application/octet-stream",
                    ["binary"] = true,
                    ["byte_length"] = binary.Length,
                    ["body"] = "",
                }));
            await socket.SendAsync(binary, WebSocketMessageType.Binary, true, CancellationToken.None);
        });
        await using var client = CreateClient(server.Uri);

        var response = await client.InvokeRuntimeAsync(
            new WorkerRuntimeRequest("runtime-binary", "GET", "/health", null, []),
            CancellationToken.None);

        Assert.Equal(binary, response.Body);
        Assert.Equal("application/octet-stream", response.ContentType);
    }

    [Fact]
    public async Task GetHealth_returns_ready_version_and_agent_capabilities()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "runtime", "voice"));
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.WorkerHealthOk,
                0,
                new JsonObject
                {
                    ["ready"] = true,
                    ["version"] = 1,
                    ["capabilities"] = new JsonArray("runtime", "voice"),
                }));
        });
        await using var client = CreateClient(server.Uri);

        var health = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(health.Ready);
        Assert.Equal(1, health.Version);
        Assert.Equal(
            new HashSet<string> { "runtime", "voice" },
            health.Capabilities);
    }

    [Fact]
    public async Task WorkerConnection_rejects_audio_ready_without_matching_binary()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.VoiceControl,
                0,
                new JsonObject { ["state"] = "audio_ready", ["byte_length"] = 4 }));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "no binary", CancellationToken.None);
        });
        await using var connection = new WorkerConnection(new DeviceWorkerClientOptions(
            server.Uri,
            "worker-secret"));
        await connection.ConnectAsync(CancellationToken.None);
        var request = new WorkerEnvelope(1, "voice-missing", WorkerMessageTypes.VoiceStart, null, []);

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await CollectAsync(connection.SendRequestAsync(request, CancellationToken.None)));

        Assert.Equal("missing_binary", error.Code);
    }

    [Fact]
    public async Task Connect_maps_an_unavailable_worker_to_a_stable_exception()
    {
        await using var client = CreateClient(new Uri("ws://127.0.0.1:65534/worker/v1"));

        await Assert.ThrowsAsync<WorkerUnavailableException>(() =>
            client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_voice_stream_sends_voice_cancel_signal()
    {
        var signalSeen = new TaskCompletionSource<(string PathAndQuery, JsonObject Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            if (request.Type == WorkerMessageTypes.VoiceStart)
            {
                while (true)
                {
                    await ReceiveEnvelopeAsync(socket);
                }
            }

            Assert.Equal(WorkerMessageTypes.RuntimeRequest, request.Type);
            var path = request.Payload["path_and_query"]?.GetValue<string>();
            var bodyText = request.Payload["body"]?.GetValue<string>() ?? "{}";
            var body = (JsonObject)(JsonNode.Parse(bodyText) ?? new JsonObject());
            signalSeen.TrySetResult((path ?? string.Empty, body));
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.RuntimeResponse,
                0,
                new JsonObject { ["status_code"] = 200, ["content_type"] = "application/json", ["body"] = "{}" }));
        });
        await using var client = CreateClient(server.Uri);
        using var cancellation = new CancellationTokenSource();
        var stream = client.StreamVoiceAsync(
            new WorkerVoiceStreamRequest("voice-cancel", "text", null, 1.0, "original"),
            cancellation.Token);

        await using var enumerator = stream.GetAsyncEnumerator();
        var moving = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);

        var signal = await signalSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("/_worker/voice/cancel", signal.PathAndQuery);
        Assert.Equal("voice-cancel", signal.Payload["request_id"]?.GetValue<string>());
    }

    private static LoopbackWorkerClient CreateClient(Uri uri, string token = "worker-secret") =>
        new(new DeviceWorkerClientOptions(uri, token));

    private static WorkerEnvelope HelloOk(string requestId, params string[] capabilities) =>
        Event(
            requestId,
            WorkerMessageTypes.HelloOk,
            0,
            new JsonObject
            {
                ["version"] = 1,
                ["capabilities"] = new JsonArray(
                    capabilities.Select(value => (JsonNode?)value).ToArray()),
            });

    private static WorkerEnvelope Event(
        string requestId,
        string type,
        long sequence,
        JsonObject payload) => new(1, requestId, type, sequence, payload);

    private static async Task CompleteHandshakeAsync(WebSocket socket)
    {
        var hello = await ReceiveEnvelopeAsync(socket);
        Assert.Equal(WorkerMessageTypes.Hello, hello.Type);
        await SendEnvelopeAsync(
            socket,
            HelloOk(hello.Id, "runtime", "voice"));
    }

    private static async Task<WorkerEnvelope> ReceiveEnvelopeAsync(WebSocket socket)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Socket closed before an envelope arrived.");
            }

            bytes.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        }
        while (!result.EndOfMessage);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return WorkerEnvelopeCodec.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    private static Task SendEnvelopeAsync(WebSocket socket, WorkerEnvelope envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(WorkerEnvelopeCodec.Serialize(envelope));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<List<WorkerEvent>> CollectAsync(
        IAsyncEnumerable<WorkerEvent> events)
    {
        var collected = new List<WorkerEvent>();
        await foreach (var item in events)
        {
            collected.Add(item);
        }

        return collected;
    }

    private sealed class FakeWorkerServer : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private FakeWorkerServer(WebApplication application, Uri uri)
        {
            _application = application;
            Uri = uri;
        }

        public Uri Uri { get; }

        public static async Task<FakeWorkerServer> StartAsync(Func<WebSocket, Task> handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            application.UseWebSockets();
            application.Map("/worker/v1", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await handler(socket);
            });
            await application.StartAsync();
            var server = application.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/worker/v1");
            return new FakeWorkerServer(application, uri);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
