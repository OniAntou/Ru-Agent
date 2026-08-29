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
        var done = WorkerV2EnvelopeCodec.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("agent.step.done", done.Type);
        Assert.Equal(1, done.Seq);
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

    private static JsonObject StartPayload() => new()
    {
        ["prompt"] = "xin chào",
        ["turns"] = new JsonArray(),
        ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(SystemPromptContext.Empty),
        ["settings"] = new JsonObject(),
    };

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
}
