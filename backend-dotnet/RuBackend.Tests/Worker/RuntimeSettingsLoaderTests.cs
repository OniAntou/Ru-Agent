using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RuBackend.Business.Settings;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class RuntimeSettingsLoaderTests
{
    [Fact]
    public async Task Load_sends_runtime_fields_without_any_credential_value()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, new WorkerEnvelope(
                1,
                observed.Id,
                WorkerMessageTypes.RuntimeState,
                0,
                new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = "Runtime loaded.",
                    ["backend"] = "OpenCode Zen",
                }));
        });
        var loader = new LoopbackRuntimeSettingsLoader(new WorkerClientOptions
        {
            Uri = server.Uri,
            Token = "worker-secret",
        });
        var candidate = Snapshot(new JsonObject
        {
            ["api_key"] = "must-not-cross-worker-boundary",
            ["future_secret"] = "must-not-be-copied",
        });

        var message = await loader.LoadAsync(candidate, CancellationToken.None);

        Assert.Equal("Runtime loaded.", message);
        Assert.Equal(WorkerMessageTypes.RuntimeLoad, observed!.Type);
        Assert.Equal("OpenCode Zen", observed.Payload["backend"]!.GetValue<string>());
        Assert.Equal("opencode/free", observed.Payload["opencode_model"]!.GetValue<string>());
        var serialized = WorkerEnvelopeCodec.Serialize(observed);
        Assert.DoesNotContain("api_key", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_maps_the_stable_worker_error_without_private_details()
    {
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            var request = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, new WorkerEnvelope(
                1,
                request.Id,
                WorkerMessageTypes.Error,
                0,
                new JsonObject
                {
                    ["code"] = "runtime_load_failed",
                    ["retryable"] = false,
                }));
        });
        var loader = new LoopbackRuntimeSettingsLoader(new WorkerClientOptions
        {
            Uri = server.Uri,
            Token = "worker-secret",
        });

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            loader.LoadAsync(Snapshot([]), CancellationToken.None));

        Assert.Equal("runtime_load_failed", error.Code);
        Assert.DoesNotContain("worker-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_forwards_the_new_backend_fields()
    {
        WorkerEnvelope? observed = null;
        await using var server = await FakeWorkerServer.StartAsync(async socket =>
        {
            await CompleteHandshakeAsync(socket);
            observed = await ReceiveEnvelopeAsync(socket);
            await SendEnvelopeAsync(socket, new WorkerEnvelope(
                1,
                observed.Id,
                WorkerMessageTypes.RuntimeState,
                0,
                new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = "Runtime loaded.",
                    ["backend"] = "Ollama",
                }));
        });
        var loader = new LoopbackRuntimeSettingsLoader(new WorkerClientOptions
        {
            Uri = server.Uri,
            Token = "worker-secret",
        });
        var candidate = Snapshot(new JsonObject()) with
        {
            Backend = "Ollama",
            OllamaModel = "qwen3:8b",
            OllamaUrl = "http://127.0.0.1:11434/v1",
            LmStudioModel = "local-model",
            CustomModel = "my-model",
            CustomUrl = "http://localhost:9000/v1",
        };

        var message = await loader.LoadAsync(candidate, CancellationToken.None);

        Assert.Equal("Runtime loaded.", message);
        Assert.Equal("Ollama", observed!.Payload["backend"]!.GetValue<string>());
        Assert.Equal("qwen3:8b", observed.Payload["ollama_model"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:11434/v1", observed.Payload["ollama_url"]!.GetValue<string>());
        Assert.Equal("local-model", observed.Payload["lmstudio_model"]!.GetValue<string>());
        Assert.Equal("my-model", observed.Payload["custom_model"]!.GetValue<string>());
        Assert.Equal("http://localhost:9000/v1", observed.Payload["custom_url"]!.GetValue<string>());
    }

    private static SettingsSnapshot Snapshot(JsonObject raw) => new(
        raw,
        "OpenCode Zen",
        "opencode/free",
        "opencode/free",
        "kilo-auto/free",
        "Default",
        256,
        0.7,
        50,
        true,
        true,
        true,
        false,
        "Ru (OmniVoice)",
        "original",
        1.0,
        false,
        true,
        false,
        "#FFFFFF",
        "medium",
        2.0,
        "auto",
        -1,
        -1,
        "qwen2.5:latest",
        "default",
        "default",
        "http://localhost:11434/v1",
        "http://localhost:1234/v1",
        "http://localhost:8000/v1");

    private static async Task CompleteHandshakeAsync(WebSocket socket)
    {
        var hello = await ReceiveEnvelopeAsync(socket);
        await SendEnvelopeAsync(socket, new WorkerEnvelope(
            1,
            hello.Id,
            WorkerMessageTypes.HelloOk,
            0,
            new JsonObject
            {
                ["version"] = 1,
                ["capabilities"] = new JsonArray("runtime"),
            }));
    }

    private static async Task<WorkerEnvelope> ReceiveEnvelopeAsync(WebSocket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Socket closed before an envelope arrived.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return WorkerEnvelopeCodec.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static Task SendEnvelopeAsync(WebSocket socket, WorkerEnvelope envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(WorkerEnvelopeCodec.Serialize(envelope));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
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
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await handler(socket);
            });
            await application.StartAsync();
            var server = application.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new FakeWorkerServer(
                application,
                new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/worker/v1"));
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
