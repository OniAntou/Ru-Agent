using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Core.Contracts;
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
                8006,
                "agent-token",
                "device-token",
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

        Assert.Equal("per-step", composition.Settings.Current.Backend);
    }

    [Fact]
    public async Task Managed_composition_serves_runtime_status_through_v1_relay()
    {
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                8006,
                "agent-token",
                "device-token",
                new ProviderBootstrapData("Ollama", "chat_completions", new Uri("http://127.0.0.1:11434/v1/chat/completions"),
                    "managed-agent",
                    string.Empty),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
            });

        var replies = await CollectAsync(composition.DeviceRouter.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            new JsonObject
            {
                ["method"] = "GET",
                ["path_and_query"] = "/status",
            },
            CancellationToken.None));

        var reply = Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, reply.Type);
        Assert.Equal(200, reply.Payload["status_code"]!.GetValue<int>());
        var body = JsonNode.Parse(reply.Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.Equal("Ollama", body["backend"]!.GetValue<string>());
    }

    [Fact]
    public async Task Managed_composition_serves_vts_and_observation_control_routes()
    {
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                8006,
                "agent-token",
                "device-token",
                new ProviderBootstrapData("Ollama", "chat_completions", new Uri("http://127.0.0.1:11434/v1/chat/completions"),
                    "managed-agent",
                    string.Empty),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
                VtubeStudioClient = new TestVtubeStudioClient(),
            });

        var vts = await CollectAsync(composition.DeviceRouter.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            new JsonObject
            {
                ["method"] = "GET",
                ["path_and_query"] = "/vts/config",
            },
            CancellationToken.None));
        var vtsReply = Assert.Single(vts);
        Assert.Equal(200, vtsReply.Payload["status_code"]!.GetValue<int>());
        var vtsBody = JsonNode.Parse(vtsReply.Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("ok", vtsBody["status"]!.GetValue<string>());
        Assert.Equal(10.0, vtsBody["config"]!["sway_amplitude_idle"]!.GetValue<double>());

        var observation = await CollectAsync(composition.DeviceRouter.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            new JsonObject
            {
                ["method"] = "GET",
                ["path_and_query"] = "/observation/status",
            },
            CancellationToken.None));
        var observationReply = Assert.Single(observation);
        Assert.Equal(200, observationReply.Payload["status_code"]!.GetValue<int>());
        var observationBody = JsonNode.Parse(observationReply.Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("disabled", observationBody["status"]!.GetValue<string>());
        Assert.False(observationBody["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Managed_composition_has_no_generic_private_route_fallback()
    {
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                8006,
                "agent-token",
                "device-token",
                new ProviderBootstrapData("Ollama", "chat_completions", new Uri("http://127.0.0.1:11434/v1/chat/completions"),
                    "managed-agent",
                    string.Empty),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
                VtubeStudioClient = new TestVtubeStudioClient(),
            });

        var routes = new[]
        {
            ("GET", "/status"),
            ("GET", "/models"),
            ("GET", "/voice/status"),
            ("POST", "/voice/latency"),
            ("POST", "/voice/tts"),
            ("POST", "/voice/warmup"),
            ("POST", "/voice/release"),
            ("POST", "/voice/transcribe"),
            ("POST", "/observation/analyze"),
            ("GET", "/vts/mouse-look"),
            ("POST", "/vts/mouse-look"),
            ("GET", "/vts/overlay"),
            ("POST", "/vts/overlay"),
            ("GET", "/vts/status"),
            ("POST", "/vts/connect"),
            ("GET", "/vts/config"),
            ("POST", "/vts/config"),
            ("POST", "/vts/activity"),
            ("POST", "/vts/action"),
            ("POST", "/vts/speaking"),
            ("POST", "/observation/preflight"),
            ("GET", "/observation/vision-config"),
            ("POST", "/observation/react"),
            ("POST", "/observation/cancel"),
            ("POST", "/observation/ack"),
            ("POST", "/observation/start"),
            ("POST", "/observation/stop"),
            ("GET", "/observation/status"),
        };

        foreach (var (method, path) in routes)
        {
            var replies = await CollectAsync(composition.DeviceRouter.RouteAsync(
                WorkerV1Contracts.RuntimeRequest,
                new JsonObject
                {
                    ["method"] = method,
                    ["path_and_query"] = path,
                    ["body"] = "{}",
                },
                CancellationToken.None));
            var reply = Assert.Single(replies);
            if (reply.Type == WorkerV1Contracts.RuntimeResponse)
            {
                Assert.DoesNotContain(
                    "Runtime route is unavailable.",
                    reply.Payload["body"]?.GetValue<string>() ?? string.Empty,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(WorkerV1Contracts.Error, reply.Type);
            }
        }
    }

    [Fact]
    public async Task Composition_keeps_agent_and_device_tokens_separate_and_does_not_echo_agent_bootstrap_to_v1()
    {
        const string agentToken = "agent-token";
        const string deviceToken = "device-token";
        const string providerCredential = "provider-secret-must-stay-v2";
        await using var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                8005,
                8006,
                agentToken,
                deviceToken,
                new ProviderBootstrapData("OpenCode Zen", "chat_completions", new Uri("https://opencode.ai/zen/v1/chat/completions"),
                    "managed-agent",
                    providerCredential),
                Directory.GetCurrentDirectory()),
            new ManagedWorkerCompositionDependencies
            {
                ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
            });

        Assert.NotSame(composition.AgentServer, composition.DeviceServer);
        Assert.Same(composition.Leases, composition.SharedLeases);
        Assert.Same(composition.Leases, composition.AgentSteps.Leases);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(context => string.Equals(context.Request.Path, "/worker/v1", StringComparison.Ordinal)
            ? composition.DeviceServer.HandleAsync(context)
            : composition.AgentServer.HandleAsync(context));
        await app.StartAsync();

        using var deviceSocket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/worker/v1"), CancellationToken.None);
        await SendAsync(
            deviceSocket,
            WorkerV1EnvelopeCodec.Serialize(new WorkerV1Envelope(
                1,
                "device-hello",
                WorkerV1Contracts.Hello,
                null,
                new JsonObject { ["token"] = deviceToken })));
        var deviceHello = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(deviceSocket));
        Assert.Equal(WorkerV1Contracts.HelloOk, deviceHello.Type);

        await SendAsync(
            deviceSocket,
            WorkerV1EnvelopeCodec.Serialize(new WorkerV1Envelope(
                1,
                "runtime-load",
                WorkerV1Contracts.RuntimeLoad,
                null,
                new JsonObject
                {
                    ["backend"] = "OpenCode Zen",
                    ["opencode_model"] = "managed-agent",
                    ["api_variant"] = "Default",
                })));
        var state = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(deviceSocket));
        Assert.Equal(WorkerV1Contracts.RuntimeState, state.Type);
        Assert.DoesNotContain(providerCredential, WorkerV1EnvelopeCodec.Serialize(state), StringComparison.Ordinal);

        await SendAsync(
            deviceSocket,
            WorkerV1EnvelopeCodec.Serialize(new WorkerV1Envelope(
                1,
                "runtime-status",
                WorkerV1Contracts.RuntimeRequest,
                null,
                new JsonObject
                {
                    ["method"] = "GET",
                    ["path_and_query"] = "/status",
                    ["body"] = "",
                })));
        var runtimeStatus = WorkerV1EnvelopeCodec.Parse(await ReceiveAsync(deviceSocket));
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, runtimeStatus.Type);
        Assert.Equal(200, runtimeStatus.Payload["status_code"]!.GetValue<int>());
        Assert.Equal(
            "ok",
            JsonNode.Parse(runtimeStatus.Payload["body"]!.GetValue<string>())!["status"]!.GetValue<string>());

        using var wrongDeviceSocket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/worker/v1"), CancellationToken.None);
        await SendAsync(
            wrongDeviceSocket,
            WorkerV1EnvelopeCodec.Serialize(new WorkerV1Envelope(
                1,
                "wrong-device-hello",
                WorkerV1Contracts.Hello,
                null,
                new JsonObject { ["token"] = agentToken })));
        var wrongDeviceClose = await ReceiveCloseAsync(wrongDeviceSocket);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, wrongDeviceClose);

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
                new JsonObject { ["token"] = deviceToken })));
        var wrongAgentClose = await ReceiveCloseAsync(wrongAgentSocket);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, wrongAgentClose);

        await app.StopAsync();
    }

    [Fact]
    public async Task OmniVoice_process_is_started_on_first_voice_call_and_stopped_with_composition()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-worker-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "omnivoice_env", "Scripts"));
        Directory.CreateDirectory(Path.Combine(root, "src", "ru_runtime", "voice"));
        await File.WriteAllTextAsync(Path.Combine(root, "omnivoice_env", "Scripts", "python.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "ru_runtime", "voice", "omnivoice_server.py"), string.Empty);

        try
        {
            var launcher = new RecordingProcessLauncher();
            var process = new OmniVoiceProcessController(
                new OmniVoiceProcessOptions(
                    root,
                    Path.Combine(root, "omnivoice_env", "Scripts", "python.exe"),
                    Path.Combine(root, "src", "ru_runtime", "voice", "omnivoice_server.py")),
                launcher);
            await using var composition = ManagedWorkerComposition.Create(
                new ManagedWorkerCompositionOptions(
                    8005,
                    8006,
                    "agent-token",
                    "device-token",
                    new ProviderBootstrapData("Ollama", "chat_completions", new Uri("http://127.0.0.1:11434/v1/chat/completions"),
                        "managed-agent",
                        string.Empty),
                    root),
                new ManagedWorkerCompositionDependencies
                {
                    OmniVoiceProcess = process,
                    VoiceHttpClient = new HttpClient(new ImmediateVoiceHandler()),
                    ModelProvider = new SequenceProvider(new ModelDelta("ok", [], true, "stop")),
                });

            Assert.Equal(0, launcher.Starts);
            Assert.NotNull(composition.VoiceProvider);
            _ = await composition.VoiceProvider!.SynthesizeAsync(
                new VoiceRequest("Xin chào", "Ru (OmniVoice)", 1.0, "original"),
                CancellationToken.None);
            Assert.Equal(1, launcher.Starts);
            Assert.True(process.IsRunning);
            await composition.DisposeAsync();
            Assert.False(process.IsRunning);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<List<WorkerV1Reply>> CollectAsync(
        IAsyncEnumerable<WorkerV1Reply> replies)
    {
        var result = new List<WorkerV1Reply>();
        await foreach (var reply in replies)
        {
            result.Add(reply);
        }

        return result;
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

    private sealed class TestVtubeStudioClient : IVtubeStudioClient
    {
        public Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VtsCatalog("model-1", "Ru", ["vts_01"], ["expr_01"]));

        public Task TriggerAsync(
            IReadOnlyList<string> catalogCodes,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ImmediateVoiceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/status")
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"model_loaded\":true}", Encoding.UTF8, "application/json"),
                });
            }

            var bytes = new byte[46];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
            BitConverter.GetBytes(16).CopyTo(bytes, 16);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
            BitConverter.GetBytes(24_000).CopyTo(bytes, 24);
            BitConverter.GetBytes(48_000).CopyTo(bytes, 28);
            BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
            BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
            BitConverter.GetBytes(2).CopyTo(bytes, 40);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingProcessLauncher : IOmniVoiceProcessLauncher
    {
        public int Starts { get; private set; }

        public IOmniVoiceProcessHandle Start(OmniVoiceProcessOptions options)
        {
            Starts++;
            return new RecordingProcessHandle();
        }
    }

    private sealed class RecordingProcessHandle : IOmniVoiceProcessHandle
    {
        public bool Killed { get; private set; }

        public bool HasExited => Killed;

        public void Kill() => Killed = true;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }
}
