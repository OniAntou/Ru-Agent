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
    public async Task Connect_sends_the_token_and_returns_negotiated_capabilities()
    {
        string? observedToken = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            observedToken = hello.Payload["token"]!.GetValue<string>();
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "chat", "memory.extract"));
        });
        await using var client = CreateClient(server.Uri, "worker-secret");

        var hello = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("worker-secret", observedToken);
        Assert.Equal(1, hello.Version);
        Assert.Equal(new HashSet<string> { "chat", "memory.extract" }, hello.Capabilities);
    }

    [Fact]
    public async Task GenerateChat_preserves_reset_chunk_done_order_and_UTF8_text()
    {
        WorkerEnvelope? observedStart = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            observedStart = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(observedStart.Id, WorkerMessageTypes.ChatReset, 0, []));
            await SendEnvelopeAsync(socket, Event(
                observedStart.Id,
                WorkerMessageTypes.ChatChunk,
                1,
                new JsonObject { ["text"] = "Em nghe đây." }));
            await SendEnvelopeAsync(socket, Event(
                observedStart.Id,
                WorkerMessageTypes.ChatDone,
                2,
                new JsonObject { ["raw_reply"] = "Em nghe đây." }));
        });
        await using var client = CreateClient(server.Uri);
        var request = new WorkerChatRequest(
            "chat-1",
            "synthetic prompt",
            "session-1",
            [new WorkerTurn("earlier", "turn")],
            "memory context",
            string.Empty,
            []);

        var events = await CollectAsync(client.GenerateChatAsync(request, CancellationToken.None));

        Assert.Equal(WorkerMessageTypes.ChatStart, observedStart!.Type);
        Assert.Equal("synthetic prompt", observedStart.Payload["prompt"]!.GetValue<string>());
        Assert.Equal(
            [WorkerMessageTypes.ChatReset, WorkerMessageTypes.ChatChunk, WorkerMessageTypes.ChatDone],
            events.Select(item => item.Type));
        Assert.Equal([0L, 1L, 2L], events.Select(item => item.Sequence));
        Assert.Equal("Em nghe đây.", events[1].Payload["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateChat_returns_tool_request_and_sends_typed_result()
    {
        WorkerEnvelope? observedResult = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(
                socket,
                Event(
                    request.Id,
                    WorkerMessageTypes.ChatToolRequest,
                    0,
                    new JsonObject
                    {
                        ["request_id"] = "tool-request-1",
                        ["session_id"] = "session",
                        ["correlation_id"] = "corr-1",
                        ["tool_id"] = "files.list",
                        ["tool_version"] = "1.0.0",
                        ["arguments"] = new JsonObject { ["path"] = "src" },
                        ["deadline_ms"] = 30_000,
                    }));
            observedResult = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(
                socket,
                Event(
                    request.Id,
                    WorkerMessageTypes.ChatDone,
                    1,
                    new JsonObject { ["raw_reply"] = "Đã xong." }));
        });

        await using var client = CreateClient(server.Uri);
        var request = EmptyChatRequest("chat-tool") with
        {
            ToolHandler = (tool, _) => Task.FromResult(
                new WorkerToolResult(
                    tool.RequestId,
                    "succeeded",
                    new JsonObject { ["entries"] = new JsonArray() },
                    null,
                    "Listed 0 entries."))
        };

        var events = await CollectAsync(client.GenerateChatAsync(request, CancellationToken.None));

        Assert.Equal(WorkerMessageTypes.ChatToolRequest, events[0].Type);
        Assert.NotNull(observedResult);
        Assert.Equal(WorkerMessageTypes.ChatToolResult, observedResult!.Type);
        Assert.Equal("tool-request-1", observedResult.Payload["request_id"]!.GetValue<string>());
        Assert.Equal("session", observedResult.Payload["session_id"]!.GetValue<string>());
        Assert.Equal("corr-1", observedResult.Payload["correlation_id"]!.GetValue<string>());
        Assert.Equal("succeeded", observedResult.Payload["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateChat_rejects_non_monotonic_sequences()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(request.Id, WorkerMessageTypes.ChatReset, 0, []));
            await SendEnvelopeAsync(socket, Event(request.Id, WorkerMessageTypes.ChatChunk, 2, []));
        });
        await using var client = CreateClient(server.Uri);
        var request = EmptyChatRequest("chat-order");

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await CollectAsync(client.GenerateChatAsync(request, CancellationToken.None)));

        Assert.Equal("invalid_sequence", error.Code);
    }

    [Fact]
    public async Task ExtractFacts_maps_the_typed_memory_response()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.MemoryFacts,
                0,
                new JsonObject
                {
                    ["facts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "preference",
                            ["content"] = "prefers concise replies",
                            ["scope"] = "D:/LLM",
                            ["confidence"] = 0.9,
                            ["importance"] = 0.7,
                        },
                    },
                }));
        });
        await using var client = CreateClient(server.Uri);

        var facts = await client.ExtractFactsAsync(
            "memory-1",
            "synthetic prompt",
            "synthetic response",
            CancellationToken.None);

        var fact = Assert.Single(facts);
        Assert.Equal("preference", fact.Kind);
        Assert.Equal("prefers concise replies", fact.Content);
        Assert.Equal("D:/LLM", fact.Scope);
        Assert.Equal(0.9, fact.Confidence);
        Assert.Equal(0.7, fact.Importance);
    }

    [Fact]
    public async Task GetVtsCatalog_maps_private_hotkey_and_expression_codes()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "vts"));
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                observed.Id,
                WorkerMessageTypes.VtsCatalog,
                0,
                new JsonObject
                {
                    ["model_id"] = "model-private",
                    ["model_name"] = "Ru",
                    ["hotkey_codes"] = new JsonArray("vts_01", "vts_02"),
                    ["expression_codes"] = new JsonArray("expr_01"),
                }));
        });
        await using var client = CreateClient(server.Uri);

        var catalog = await client.GetVtsCatalogAsync("vts-catalog-1", CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(new HashSet<string> { "vts_01", "vts_02" }, catalog.HotkeyCodes);
        Assert.Equal(new HashSet<string> { "expr_01" }, catalog.ExpressionCodes);
        Assert.Equal(WorkerMessageTypes.VtsCatalog, observed!.Type);
        Assert.Equal("catalog", observed.Payload["operation"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetVtsCatalog_returns_null_when_vts_is_unavailable()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "vts"));
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.Error,
                0,
                new JsonObject
                {
                    ["code"] = "vts_unavailable",
                    ["retryable"] = true,
                }));
        });
        await using var client = CreateClient(server.Uri);

        var catalog = await client.GetVtsCatalogAsync("vts-catalog-none", CancellationToken.None);

        Assert.Null(catalog);
    }

    [Fact]
    public async Task TriggerVtsActions_sends_only_typed_private_codes()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "vts"));
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                observed.Id,
                WorkerMessageTypes.VtsAction,
                0,
                new JsonObject { ["status"] = "ok" }));
        });
        await using var client = CreateClient(server.Uri);

        await client.TriggerVtsActionsAsync(
            "vts-action-1",
            ["vts_01", "expr_01"],
            CancellationToken.None);

        Assert.Equal(WorkerMessageTypes.VtsAction, observed!.Type);
        Assert.Equal("action", observed.Payload["operation"]!.GetValue<string>());
        Assert.Equal(
            ["vts_01", "expr_01"],
            observed.Payload["codes"]!.AsArray().Select(item => item!.GetValue<string>()));
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
                "/observation/cancel",
                "application/json",
                Encoding.UTF8.GetBytes("{\"observation_id\":\"obs-1\"}")),
            CancellationToken.None);

        Assert.Equal(WorkerMessageTypes.RuntimeRequest, observed!.Type);
        Assert.Equal("POST", observed.Payload["method"]!.GetValue<string>());
        Assert.Equal(
            "{\"observation_id\":\"obs-1\"}",
            observed.Payload["body"]!.GetValue<string>());
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("worker", response.Headers!["x-runtime"]);
    }

    [Fact]
    public async Task GetVtsCatalog_rejects_a_malformed_private_response()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            var hello = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, HelloOk(hello.Id, "vts"));
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.VtsCatalog,
                0,
                new JsonObject
                {
                    ["hotkey_codes"] = "private malformed value",
                    ["expression_codes"] = new JsonArray(),
                }));
        });
        await using var client = CreateClient(server.Uri);

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            client.GetVtsCatalogAsync("vts-catalog-invalid", CancellationToken.None));

        Assert.Equal("invalid_vts_catalog", error.Code);
        Assert.DoesNotContain("private malformed value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_chat_sends_chat_cancel_for_the_same_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            await ReceiveEnvelopeAsync(socket);
            started.SetResult();
            cancelled.SetResult(await ReceiveEnvelopeAsync(socket));
        });
        await using var client = CreateClient(server.Uri);
        using var cancellation = new CancellationTokenSource();
        var collect = CollectAsync(client.GenerateChatAsync(
            EmptyChatRequest("chat-cancel"),
            cancellation.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collect);
        var cancelEnvelope = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkerMessageTypes.ChatCancel, cancelEnvelope.Type);
        Assert.Equal("chat-cancel", cancelEnvelope.Id);
    }

    [Fact]
    public async Task GetHealth_returns_ready_version_and_capabilities()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, Event(
                request.Id,
                WorkerMessageTypes.WorkerHealthOk,
                0,
                new JsonObject
                {
                    ["ready"] = true,
                    ["version"] = 1,
                    ["capabilities"] = new JsonArray("chat", "voice"),
                }));
        });
        await using var client = CreateClient(server.Uri);

        var health = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(health.Ready);
        Assert.Equal(1, health.Version);
        Assert.Equal(new HashSet<string> { "chat", "voice" }, health.Capabilities);
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
                new JsonObject
                {
                    ["state"] = "audio_ready",
                    ["byte_length"] = 4,
                }));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "no binary", CancellationToken.None);
        });
        await using var connection = new WorkerConnection(new WorkerClientOptions
        {
            Uri = server.Uri,
            Token = "worker-secret",
        });
        await connection.ConnectAsync(CancellationToken.None);
        var request = new WorkerEnvelope(
            1,
            "voice-missing",
            WorkerMessageTypes.VoiceStart,
            null,
            []);

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
    public async Task Cancelling_voice_stream_sends_voice_cancel_signal_not_chat_cancel()
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
                new JsonObject
                {
                    ["status_code"] = 200,
                    ["content_type"] = "application/json",
                    ["body"] = "{}",
                }));
        });
        await using var client = CreateClient(server.Uri);
        using var cancellation = new CancellationTokenSource();
        var stream = client.StreamVoiceAsync(
            new WorkerVoiceStreamRequest("voice-cancel", "text", null, 1.0, "original"),
            cancellation.Token);

        var enumerator = stream.GetAsyncEnumerator();
        var moving = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);

        var signal = await signalSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("/_worker/voice/cancel", signal.PathAndQuery);
        Assert.Equal("voice-cancel", signal.Payload["request_id"]?.GetValue<string>());
    }

    private static LoopbackWorkerClient CreateClient(Uri uri, string token = "worker-secret") =>
        new(new WorkerClientOptions { Uri = uri, Token = token });

    private static WorkerChatRequest EmptyChatRequest(string requestId) =>
        new(requestId, "prompt", "session", [], string.Empty, string.Empty, []);

    private static WorkerEnvelope HelloOk(string requestId, params string[] capabilities) =>
        Event(
            requestId,
            WorkerMessageTypes.HelloOk,
            0,
            new JsonObject
            {
                ["version"] = 1,
                ["capabilities"] = new JsonArray(capabilities.Select(value => (JsonNode?)value).ToArray()),
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
            HelloOk(hello.Id, "chat", "memory.extract", "runtime", "voice"));
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
