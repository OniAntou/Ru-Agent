using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class ManagedWorkerCompositionTests
{
    [Fact]
    public async Task Managed_composition_accepts_internal_per_step_bootstrap_without_provider_fallback()
    {
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                "agent-token",
                new ProviderBootstrapData(
                    "per-step",
                    ProviderProtocols.ChatCompletions,
                    new Uri("http://127.0.0.1/"),
                    "per-step-required",
                    string.Empty),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
            });

        Assert.NotNull(composition.AgentSteps);
        Assert.NotNull(composition.AgentServer);
    }

    [Fact]
    public async Task Composition_serves_the_agent_token_and_rejects_others()
    {
        const string agentToken = "agent-token";
        const string providerCredential = "provider-secret-must-stay-private";
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                agentToken,
                new ProviderBootstrapData("OpenCode Zen", "chat_completions", new Uri("https://opencode.ai/zen/v1/chat/completions"),
                    "managed-agent",
                    providerCredential),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
            });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(composition.AgentServer.HandleAsync);
        await app.StartAsync();

        using var agentSocket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendAsync(
            agentSocket,
            WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
                2,
                "agent-hello",
                WorkerV2Contracts.Hello,
                null,
                new JsonObject { ["token"] = agentToken })));
        var agentHello = WorkerV2EnvelopeCodec.Parse(await ReceiveAsync(agentSocket));
        Assert.Equal(WorkerV2Contracts.HelloOk, agentHello.Type);

        using var wrongAgentSocket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);
        await SendAsync(
            wrongAgentSocket,
            WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
                2,
                "wrong-agent-hello",
                WorkerV2Contracts.Hello,
                null,
                new JsonObject { ["token"] = "wrong-token" })));
        var wrongAgentClose = await ReceiveCloseAsync(wrongAgentSocket);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, wrongAgentClose);

        await app.StopAsync();
    }

    private static async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[128 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Unexpected close frame.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(WebSocket socket)
    {
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return result.MessageType == WebSocketMessageType.Close ? socket.CloseStatus : null;
    }

    private sealed class SequenceProvider : IModelProvider
    {
        private readonly ModelDelta _delta;

        public SequenceProvider(ModelDelta delta) => _delta = delta;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return _delta;
        }
    }
}
