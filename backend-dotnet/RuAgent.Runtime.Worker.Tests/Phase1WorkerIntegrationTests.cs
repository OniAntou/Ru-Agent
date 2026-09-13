using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

/// <summary>
/// One real in-process Worker v2 WebSocket lifecycle with a fake provider,
/// plus the Phase 1 privacy scans over every serialized frame. No live model,
/// microphone, audio output, or device connection is used.
/// </summary>
public sealed class Phase1WorkerIntegrationTests
{
    private const string PrivatePrompt = "integration prompt must stay private";
    private const string ProviderCredential = "integration-provider-secret";

    [Fact]
    public async Task Hello_start_and_done_round_trip_with_receipt_and_delta_parity()
    {
        var context = SystemPromptContext.Empty;
        await using var host = await StartHostAsync(new SequenceProvider(
            new ModelDelta("xin ", [], false, null),
            new ModelDelta("chao ", [], false, null),
            new ModelDelta("anh", [], true, "stop")));

        using var socket = await ConnectAsync(host);
        var frames = new List<string>();
        await HelloAsync(socket);
        frames.Add(await ReceiveAsync(socket));
        await SendAsync(socket, StartEnvelope("step-1", context));
        while (true)
        {
            var frame = await ReceiveAsync(socket);
            frames.Add(frame);
            if (WorkerV2EnvelopeCodec.Parse(frame).Type == WorkerV2Contracts.AgentStepDone)
            {
                break;
            }
        }

        var done = WorkerV2EnvelopeCodec.Parse(frames[^1]);
        Assert.Equal("agent.step.done", done.Type);
        Assert.Equal(4, done.Seq);
        Assert.Equal("xin chao anh", done.Payload["text"]!.GetValue<string>());
        Assert.Equal("stop", done.Payload["finish_reason"]!.GetValue<string>());
        Assert.Equal(
            new[] { "agent.step.delta", "agent.step.delta", "agent.step.delta", "agent.step.done" },
            frames.Skip(1).Select(frame => WorkerV2EnvelopeCodec.Parse(frame).Type));

        // The worker composes with the same canonical composer as Core.
        var expectedReceipt = SystemPromptComposer.Compose(context).Receipt;
        var receipt = done.Payload["prompt_receipt"]!;
        Assert.Equal(expectedReceipt.PromptSha256, receipt["prompt_sha256"]!.GetValue<string>());
        Assert.Equal(
            expectedReceipt.BlockDigests.Select(item => item.BlockId),
            ((JsonArray)receipt["block_digests"]!).Select(item => item!["block_id"]!.GetValue<string>()));

        // Done carries exactly the approved fields; no prompt/settings echo.
        Assert.Equal(
            new[] { "finish_reason", "prompt_receipt", "text", "tool_calls" },
            done.Payload.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));

        foreach (var frame in frames)
        {
            Assert.DoesNotContain(PrivatePrompt, frame, StringComparison.Ordinal);
            Assert.DoesNotContain(ProviderCredential, frame, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tool_arguments_stay_confined_to_the_done_tool_calls_field()
    {
        await using var host = await StartHostAsync(new SequenceProvider(
            new ModelDelta(
                null,
                [new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\",\"limit\":64}")],
                true,
                "tool_calls")));

        using var socket = await ConnectAsync(host);
        await HelloAsync(socket);
        _ = await ReceiveAsync(socket);
        await SendAsync(socket, StartEnvelope("step-tool", SystemPromptContext.Empty, includeTool: true));
        var rawDone = await ReceiveAsync(socket);

        var done = WorkerV2EnvelopeCodec.Parse(rawDone);
        var toolCall = done.Payload["tool_calls"]!.AsArray().Single()!.AsObject();
        Assert.Equal("files.read", toolCall["tool_id"]!.GetValue<string>());
        Assert.Equal("README.md", toolCall["arguments"]!["path"]!.GetValue<string>());

        // Arguments never leak into the digest-only receipt.
        var receiptJson = done.Payload["prompt_receipt"]!.ToJsonString();
        Assert.DoesNotContain("README.md", receiptJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_returns_a_bounded_cancelled_error_without_prompt_leakage()
    {
        var provider = new BlockingProvider();
        await using var host = await StartHostAsync(provider);
        using var socket = await ConnectAsync(host);
        await HelloAsync(socket);
        _ = await ReceiveAsync(socket);

        await SendAsync(socket, StartEnvelope("step-cancel", SystemPromptContext.Empty));
        var receiveTask = ReceiveAsync(socket);
        await provider.StartedSignal.Task;
        await SendAsync(
            socket,
            WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
                2, "step-cancel", "agent.step.cancel", null, new JsonObject())));
        var rawError = await receiveTask;

        var error = WorkerV2EnvelopeCodec.Parse(rawError);
        Assert.Equal("error", error.Type);
        Assert.Equal("cancelled", error.Payload["code"]!.GetValue<string>());
        Assert.False(error.Payload["retryable"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "code", "retryable" },
            error.Payload.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.DoesNotContain(PrivatePrompt, rawError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_auth_failure_maps_to_a_bounded_error_without_credentials()
    {
        await using var host = await StartHostAsync(new ThrowingProvider(
            new ModelProviderException("auth_failed", retryable: false)));
        using var socket = await ConnectAsync(host);
        await HelloAsync(socket);
        _ = await ReceiveAsync(socket);
        await SendAsync(socket, StartEnvelope("step-auth", SystemPromptContext.Empty));
        var rawError = await ReceiveAsync(socket);

        var error = WorkerV2EnvelopeCodec.Parse(rawError);
        Assert.Equal("provider_auth", error.Payload["code"]!.GetValue<string>());
        Assert.False(error.Payload["retryable"]!.GetValue<bool>());
        Assert.DoesNotContain(ProviderCredential, rawError, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivatePrompt, rawError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_paths_and_malformed_frames_fail_closed_without_payload_echo()
    {
        await using var host = await StartHostAsync(new SequenceProvider(
            new ModelDelta("unused", [], true, "stop")));

        using var socket = await ConnectAsync(host);
        await HelloAsync(socket);
        _ = await ReceiveAsync(socket);
        await SendAsync(socket, "{\"v\":2,\"id\":\"bad\",\"id\":\"bad\",\"type\":\"hello\",\"seq\":0,\"payload\":{}}");
        var rawError = await ReceiveAsync(socket);

        var error = WorkerV2EnvelopeCodec.Parse(rawError);
        Assert.Equal("error", error.Type);
        Assert.Equal(
            new[] { "code", "retryable" },
            error.Payload.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    private static async Task<WebApplication> StartHostAsync(IModelProvider provider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseWebSockets();
        var server = new WorkerV2Server(
            "worker-secret",
            new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker-secret",
                    new Uri("http://127.0.0.1:9/v1"),
                    "managed-agent",
                    ProviderCredential)));
        app.Run(server.HandleAsync);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebSocket> ConnectAsync(WebApplication host) =>
        await host.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/worker/v2"), CancellationToken.None);

    private static async Task HelloAsync(WebSocket socket) =>
        await SendAsync(
            socket,
            WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
                2, "hello-1", "hello", null, new JsonObject { ["token"] = "worker-secret" })));

    private static string StartEnvelope(string id, SystemPromptContext context, bool includeTool = false)
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

        return WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
            2,
            id,
            "agent.step.start",
            null,
            new JsonObject
            {
                ["prompt"] = PrivatePrompt,
                ["turns"] = new JsonArray(),
                ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(context),
                ["settings"] = settings,
            }));
    }

    private static async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new byte[256 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private sealed class SequenceProvider(params ModelDelta[] deltas) : IModelProvider
    {
        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public TaskCompletionSource StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StartedSignal.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ThrowingProvider(ModelProviderException error) : IModelProvider
    {
        public IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken) => throw error;
    }
}
