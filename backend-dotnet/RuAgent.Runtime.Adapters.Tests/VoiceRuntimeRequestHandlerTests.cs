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

    private sealed class FakeVoiceProvider : IVoiceProvider, IVoiceLifecycle
    {
        public VoiceAudio? Audio { get; init; }
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
