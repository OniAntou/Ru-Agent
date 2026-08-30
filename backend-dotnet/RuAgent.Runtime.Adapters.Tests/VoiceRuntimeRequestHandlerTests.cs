using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class VoiceRuntimeRequestHandlerTests
{
    [Fact]
    public async Task Tts_returns_a_binary_wav_response_without_provider_error_text()
    {
        var provider = new FakeVoiceProvider
        {
            Audio = new VoiceAudio(ValidWav(), 24_000, 1),
        };
        var handler = new VoiceRuntimeRequestHandler(provider);
        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["text"] = "Xin chào",
                    ["voice_label"] = "Ru (OmniVoice)",
                    ["speed"] = 1.0,
                }.ToJsonString())),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Binary);
        Assert.Equal("audio/wav", response.ContentType);
        Assert.Equal(ValidWav(), response.Body.ToArray());
    }

    [Fact]
    public async Task Warmup_and_release_use_optional_provider_lifecycle()
    {
        var provider = new FakeVoiceProvider { Audio = new VoiceAudio(ValidWav(), 24_000, 1) };
        var handler = new VoiceRuntimeRequestHandler(provider);

        var warmup = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/warmup",
                "application/json",
                Encoding.UTF8.GetBytes("{\"voice_label\":\"Ru (OmniVoice)\"}")),
            CancellationToken.None);
        var release = await handler.InvokeAsync(
            new PrivateRuntimeRequest("POST", "/voice/release", null, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);

        Assert.Equal(200, warmup.StatusCode);
        Assert.Equal(200, release.StatusCode);
        Assert.Equal(1, provider.Warmups);
        Assert.Equal(1, provider.Releases);
    }

    [Fact]
    public async Task Invalid_tts_input_is_rejected_before_provider_invocation()
    {
        var provider = new FakeVoiceProvider { Audio = new VoiceAudio(ValidWav(), 24_000, 1) };
        var handler = new VoiceRuntimeRequestHandler(provider);
        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/tts",
                "application/json",
                Encoding.UTF8.GetBytes("{\"text\":\"\",\"speed\":9}")),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.False(response.Binary);
        Assert.Equal(0, provider.Syntheses);
    }

    [Fact]
    public async Task Status_does_not_advertise_an_unready_provider()
    {
        var handler = new VoiceRuntimeRequestHandler(new FakeVoiceProvider
        {
            Audio = new VoiceAudio(ValidWav(), 24_000, 1),
            IsReady = false,
        });

        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest("GET", "/voice/status", null, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        var body = JsonNode.Parse(response.Body.Span)!.AsObject();
        Assert.False(body["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_uses_the_managed_voice_settings_snapshot()
    {
        var settings = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false)
            {
                Voice = new VoiceSettingsSnapshot(
                    "Ái Vy (OmniVoice)",
                    "original",
                    1.25,
                    true,
                    false,
                    true,
                    "#ABCDEF",
                    "large",
                    3.5,
                    "manual",
                    12,
                    34),
            });
        var handler = new VoiceRuntimeRequestHandler(
            new FakeVoiceProvider { Audio = new VoiceAudio(ValidWav(), 24_000, 1) },
            settings);

        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest("GET", "/voice/status", null, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);

        var body = JsonNode.Parse(response.Body.Span)!.AsObject();
        Assert.Equal("Ái Vy (OmniVoice)", body["voice_label"]!.GetValue<string>());
        Assert.Equal(1.25, body["voice_speed"]!.GetValue<double>());
        Assert.True(body["show_subtitles"]!.GetValue<bool>());
        Assert.Equal("#ABCDEF", body["subtitle_color"]!.GetValue<string>());
        Assert.Equal(12, body["subtitle_pos_x"]!.GetValue<int>());
        Assert.Equal(34, body["subtitle_pos_y"]!.GetValue<int>());
    }

    [Fact]
    public async Task Latency_rejects_unknown_fields_instead_of_treating_them_as_timings()
    {
        var handler = new VoiceRuntimeRequestHandler(new FakeVoiceProvider
        {
            Audio = new VoiceAudio(ValidWav(), 24_000, 1),
        });

        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/latency",
                "application/json",
                Encoding.UTF8.GetBytes("{\"outcome\":\"completed\",\"unknown\":1}")),
            CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
    }

    private static byte[] ValidWav()
    {
        var data = new byte[46];
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

    private sealed class FakeVoiceProvider : IVoiceProvider, IVoiceLifecycle, IVoiceReadiness
    {
        public VoiceAudio? Audio { get; init; }
        public bool IsReady { get; init; } = true;
        public int Syntheses { get; private set; }
        public int Warmups { get; private set; }
        public int Releases { get; private set; }

        public Task<VoiceAudio> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
        {
            Syntheses++;
            return Task.FromResult(Audio!);
        }

        public async IAsyncEnumerable<VoiceChunk> StreamAsync(
            VoiceRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new VoiceChunk("audio", 0, Audio!.WavBytes);
            yield return new VoiceChunk("ended", 1);
        }

        public Task WarmupAsync(string voiceProfile, CancellationToken cancellationToken)
        {
            Warmups++;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }
}
