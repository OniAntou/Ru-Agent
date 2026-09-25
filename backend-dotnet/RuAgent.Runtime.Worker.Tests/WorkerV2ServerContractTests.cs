using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerV2ServerContractTests
{
    [Fact]
    public async Task Server_authenticates_hello_and_emits_monotonic_done_sequence()
    {
        await using var host = await StartHostAsync(new FakeModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));

        var hello = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("hello.ok", hello.Type);
        Assert.Equal(0, hello.Seq);

        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "request-1", "agent.step.start", null, StartPayload())));
        var delta = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("agent.step.delta", delta.Type);
        Assert.Equal(1, delta.Seq);
        Assert.Equal("done", delta.Payload["text"]!.GetValue<string>());
        var done = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("agent.step.done", done.Type);
        Assert.Equal(2, done.Seq);
    }

    [Fact]
    public async Task Server_preserves_native_call_order_and_assigns_missing_request_ids()
    {
        await using var host = await StartHostAsync(new FakeModelProvider(
            new ModelDelta("I will inspect both files.", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment(null, "files.read", "{\"path\":\"README.md\"}"),
                    new ModelToolFragment("call-2", "files.read", "{\"path\":\"CHANGELOG.md\"}"),
                },
                true,
                "tool_calls")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));

        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
            2,
            "step-multi",
            "agent.step.start",
            null,
            StartPayload(includeTool: true))));

        var delta = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("agent.step.delta", delta.Type);
        Assert.Equal("I will inspect both files.", delta.Payload["text"]!.GetValue<string>());

        var done = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("agent.step.done", done.Type);
        Assert.Equal("I will inspect both files.", done.Payload["text"]!.GetValue<string>());
        var calls = done.Payload["tool_calls"]!.AsArray();
        Assert.Equal(2, calls.Count);
        Assert.Equal("ru_call_step-multi_1", calls[0]!["provider_call_id"]!.GetValue<string>());
        Assert.Equal("README.md", calls[0]!["arguments"]!["path"]!.GetValue<string>());
        Assert.Equal("call-2", calls[1]!["provider_call_id"]!.GetValue<string>());
        Assert.Equal("CHANGELOG.md", calls[1]!["arguments"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_rejects_wrong_token_without_exposing_secret()
    {
        await using var host = await StartHostAsync(new FakeModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "wrong" })));

        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    public async Task Server_maps_non_text_control_frames_to_a_bounded_error()
    {
        await using var host = await StartHostAsync(new FakeModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));

        await socket.SendAsync(new ArraySegment<byte>([1, 2, 3]), WebSocketMessageType.Binary, true, CancellationToken.None);

        var error = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("error", error.Type);
        Assert.Equal("connection", error.Id);
        Assert.Equal("text_control_required", error.Payload["code"]!.GetValue<string>());
        Assert.False(error.Payload["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Server_rejects_invalid_utf8_control_frames_without_replacement_text()
    {
        await using var host = await StartHostAsync(new FakeModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await socket.SendAsync(new ArraySegment<byte>([0xC3, 0x28]), WebSocketMessageType.Text, true, CancellationToken.None);

        var error = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("error", error.Type);
        Assert.Equal("connection", error.Id);
        Assert.Equal("invalid_utf8", error.Payload["code"]!.GetValue<string>());
        Assert.False(error.Payload["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Server_runs_back_to_back_requests_without_wedging_the_progress_gate()
    {
        await using var host = await StartHostAsync(new ImmediateModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));

        for (var i = 1; i <= 5; i++)
        {
            await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, $"request-{i}", "agent.step.start", null, StartPayload())));

            var delta = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
            Assert.Equal("agent.step.delta", delta.Type);
            var done = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
            Assert.Equal("agent.step.done", done.Type);
            Assert.Equal($"request-{i}", done.Id);
        }
    }

    [Fact]
    public async Task Server_rejects_a_duplicate_request_id()
    {
        await using var host = await StartHostAsync(new ImmediateModelProvider(new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop")));
        using var socket = await host.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));

        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "request-dup", "agent.step.start", null, StartPayload())));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        _ = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));

        await SendTextAsync(socket, WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(2, "request-dup", "agent.step.start", null, StartPayload())));

        var error = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("error", error.Type);
        Assert.Equal("request-dup", error.Id);
        Assert.Equal("duplicate_request", error.Payload["code"]!.GetValue<string>());
        Assert.False(error.Payload["retryable"]!.GetValue<bool>());
    }

    private static async Task<WebApplication> StartHostAsync(IModelProvider provider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseWebSockets();
        var server = new WorkerV2Server("worker-secret", new WorkerV2StepService(provider));
        app.Run(server.HandleAsync);
        await app.StartAsync();
        return app;
    }

    private static JsonObject StartPayload(bool includeTool = false)
    {
        var settings = new JsonObject();
        if (includeTool)
        {
            settings["model_tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool_id"] = "files.read",
                    ["tool_version"] = "1.0.0",
                    ["scopes"] = new JsonArray("workspace.read"),
                    ["availability"] = "executable",
                    ["model_enabled"] = true,
                    ["capability_class"] = "tool",
                    ["verification_required"] = false,
                },
            };
        }

        return new JsonObject
        {
            ["prompt"] = "xin chào",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(SystemPromptContext.Empty),
            ["settings"] = settings,
        };
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[128 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelDelta> _deltas;
        public FakeModelProvider(params ModelDelta[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class ImmediateModelProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelDelta> _deltas;
        public ImmediateModelProvider(params ModelDelta[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return delta;
            }
        }
    }
}
