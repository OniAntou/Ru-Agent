using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class OmniVoiceProviderTests
{
    [Fact]
    public async Task Process_controller_starts_once_and_stops_owned_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-omnivoice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var script = Path.Combine(root, "omnivoice_server.py");
        await File.WriteAllTextAsync(python, string.Empty);
        await File.WriteAllTextAsync(script, string.Empty);
        try
        {
            var launcher = new FakeProcessLauncher();
            await using var controller = new OmniVoiceProcessController(
                new OmniVoiceProcessOptions(root, python, script),
                launcher);

            await controller.StartAsync(CancellationToken.None);
            await controller.StartAsync(CancellationToken.None);
            Assert.Equal(1, launcher.Starts);
            Assert.True(controller.IsRunning);

            await controller.StopAsync(CancellationToken.None);
            Assert.False(controller.IsRunning);
            Assert.True(launcher.Handle!.Killed);
            Assert.True(launcher.Handle.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Options_reject_non_loopback_provider_uri()
    {
        Assert.Throws<ArgumentException>(() => new OmniVoiceProviderOptions(new Uri("https://example.test")));
    }

    [Fact]
    public async Task Synthesize_posts_profile_speed_and_original_style()
    {
        var handler = new RecordingHandler(_ => AudioResponse(ValidWav()));
        var provider = new OmniVoiceHttpProvider(
            new HttpClient(handler),
            new OmniVoiceProviderOptions(new Uri("http://127.0.0.1:5052")));

        var audio = await provider.SynthesizeAsync(
            new VoiceRequest("Chào anh.", "Ái Vy (OmniVoice)", 1.05, "original"),
            CancellationToken.None);

        Assert.Equal(24_000, audio.SampleRateHz);
        Assert.Equal(1, audio.Channels);
        Assert.Equal("POST", handler.Request!.Method.Method);
        Assert.Equal("http://127.0.0.1:5052/tts", handler.Request.RequestUri!.ToString());
        var body = handler.RequestBody!;
        var payload = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("Chào anh.", payload["text"]!.GetValue<string>());
        Assert.Equal("ai_vy", payload["voice_profile"]!.GetValue<string>());
        Assert.Equal(1.05, payload["speed"]!.GetValue<double>());
        Assert.Equal("original", payload["voice_style_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Managed_provider_checks_readiness_and_warms_unloaded_model_before_tts()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-omnivoice-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var script = Path.Combine(root, "omnivoice_server.py");
        await File.WriteAllTextAsync(python, string.Empty);
        await File.WriteAllTextAsync(script, string.Empty);
        try
        {
            var launcher = new FakeProcessLauncher();
            await using var controller = new OmniVoiceProcessController(
                new OmniVoiceProcessOptions(root, python, script),
                launcher);
            var handler = new SequenceHandler(
                JsonResponse("{\"model_loaded\":false}"),
                JsonResponse("{\"ready\":true}"),
                AudioResponse(ValidWav()));
            var provider = new OmniVoiceHttpProvider(
                new HttpClient(handler),
                new OmniVoiceProviderOptions(new Uri("http://127.0.0.1:5052")),
                controller);

            _ = await provider.SynthesizeAsync(
                new VoiceRequest("Chào anh.", "Ái Vy (OmniVoice)", 1.0, "original"),
                CancellationToken.None);

            Assert.Equal(["GET /status", "POST /warmup", "POST /tts"], handler.Requests);
            Assert.Equal(1, launcher.Starts);
            Assert.Equal("{\"voice_profile\":\"ai_vy\"}", handler.RequestBodies[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_provider_maps_failed_readiness_to_unavailable_without_tts()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-omnivoice-not-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var script = Path.Combine(root, "omnivoice_server.py");
        await File.WriteAllTextAsync(python, string.Empty);
        await File.WriteAllTextAsync(script, string.Empty);
        try
        {
            var launcher = new FakeProcessLauncher();
            await using var controller = new OmniVoiceProcessController(
                new OmniVoiceProcessOptions(root, python, script),
                launcher);
            var handler = new SequenceHandler(
                JsonResponse("{\"model_loaded\":false}"),
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"warmup failed\"}", Encoding.UTF8, "application/json"),
                });
            var provider = new OmniVoiceHttpProvider(
                new HttpClient(handler),
                new OmniVoiceProviderOptions(new Uri("http://127.0.0.1:5052"))
                {
                    StartupTimeout = TimeSpan.FromMilliseconds(100),
                    ReadinessTimeout = TimeSpan.FromMilliseconds(50),
                },
                controller);

            var error = await Assert.ThrowsAsync<OmniVoiceProviderException>(() => provider.SynthesizeAsync(
                new VoiceRequest("Chào anh.", "Ru (OmniVoice)", 1.0, "original"),
                CancellationToken.None));

            Assert.Equal("voice_unavailable", error.Code);
            Assert.Equal(["GET /status", "POST /warmup"], handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_warmup_requires_a_ready_acknowledgement()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-omnivoice-warmup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var python = Path.Combine(root, "python.exe");
        var script = Path.Combine(root, "omnivoice_server.py");
        await File.WriteAllTextAsync(python, string.Empty);
        await File.WriteAllTextAsync(script, string.Empty);
        try
        {
            var launcher = new FakeProcessLauncher();
            await using var controller = new OmniVoiceProcessController(
                new OmniVoiceProcessOptions(root, python, script),
                launcher);
            var handler = new SequenceHandler(JsonResponse("{\"ready\":false}"));
            var provider = new OmniVoiceHttpProvider(
                new HttpClient(handler),
                new OmniVoiceProviderOptions(new Uri("http://127.0.0.1:5052")),
                controller);

            var error = await Assert.ThrowsAsync<OmniVoiceProviderException>(() => provider.WarmupAsync(
                "Ru (OmniVoice)",
                CancellationToken.None));

            Assert.Equal("voice_unavailable", error.Code);
            Assert.False(provider.IsReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Synthesize_rejects_invalid_wav_without_returning_bytes()
    {
        var handler = new RecordingHandler(_ => AudioResponse(Encoding.UTF8.GetBytes("not-wav")));
        var provider = new OmniVoiceHttpProvider(
            new HttpClient(handler),
            new OmniVoiceProviderOptions(new Uri("http://localhost:5052")));

        var error = await Assert.ThrowsAsync<OmniVoiceProviderException>(() => provider.SynthesizeAsync(
            new VoiceRequest("Chào anh.", "Ru (OmniVoice)", 1.0, "original"),
            CancellationToken.None));

        Assert.Equal("invalid_audio", error.Code);
    }

    [Fact]
    public async Task Stream_returns_a_complete_wav_as_one_ordered_audio_chunk()
    {
        var handler = new RecordingHandler(_ => AudioResponse(ValidWav()));
        var provider = new OmniVoiceHttpProvider(
            new HttpClient(handler),
            new OmniVoiceProviderOptions(new Uri("http://127.0.0.1:5052")));

        var chunks = new List<VoiceChunk>();
        await foreach (var chunk in provider.StreamAsync(
            new VoiceRequest("Chào anh.", "Ru (OmniVoice)", 1.0, "original"),
            CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["audio", "ended"], chunks.Select(chunk => chunk.State));
        Assert.Equal(0, chunks[0].Sequence);
        Assert.Equal(1, chunks[1].Sequence);
    }

    private static HttpResponseMessage AudioResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        return response;
    }

    private static byte[] ValidWav()
    {
        var data = new byte[44 + 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        BitConverter.GetBytes(data.Length - 8).CopyTo(data, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(data, 8);
        BitConverter.GetBytes(16).CopyTo(data, 16);
        BitConverter.GetBytes((short)1).CopyTo(data, 20);
        BitConverter.GetBytes((short)1).CopyTo(data, 22);
        BitConverter.GetBytes(24_000).CopyTo(data, 24);
        BitConverter.GetBytes(48_000).CopyTo(data, 28);
        BitConverter.GetBytes((short)2).CopyTo(data, 32);
        BitConverter.GetBytes((short)16).CopyTo(data, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(data, 36);
        BitConverter.GetBytes(2).CopyTo(data, 40);
        return data;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        private HttpResponseMessage? _response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            _response = _factory(request);
            return Task.FromResult(_response);
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses) => _responses = new(responses);

        public List<string> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            RequestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake OmniVoice response remains.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private sealed class FakeProcessLauncher : IOmniVoiceProcessLauncher
    {
        public int Starts { get; private set; }
        public FakeProcessHandle? Handle { get; private set; }

        public IOmniVoiceProcessHandle Start(OmniVoiceProcessOptions options)
        {
            Starts++;
            Handle = new FakeProcessHandle();
            return Handle;
        }
    }

    private sealed class FakeProcessHandle : IOmniVoiceProcessHandle
    {
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool HasExited => Killed;

        public void Kill() => Killed = true;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => Disposed = true;
    }
}
