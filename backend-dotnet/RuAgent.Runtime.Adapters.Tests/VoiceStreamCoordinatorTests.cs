using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class VoiceStreamCoordinatorTests
{
    [Fact]
    public async Task Coordinator_preserves_first_audio_order_and_releases_lease()
    {
        var provider = new FakeVoiceProvider
        {
            Stream = _ => Async(
                new VoiceChunk("audio", 44, new byte[] { 1 }),
                new VoiceChunk("audio", 45, new byte[] { 2 }),
                new VoiceChunk("ended", 46)),
        };
        await using var leases = new RuntimeLeaseCoordinator();
        var coordinator = new VoiceStreamCoordinator(provider, leases);

        var output = await CollectAsync(coordinator.StreamAsync(
            new VoiceRequest("Một câu. Câu hai.", "Ru (OmniVoice)", 1.0, "original"),
            CancellationToken.None));

        Assert.Equal(["audio", "audio", "ended"], output.Select(chunk => chunk.State));
        Assert.Equal([0L, 1L, 2L], output.Select(chunk => chunk.Sequence));
        Assert.Equal(new byte[] { 1 }, output[0].Audio.ToArray());

        await using var next = await leases.AcquireAsync(ResourceKind.GpuAudio, CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_falls_back_to_complete_synthesis_before_first_stream_chunk()
    {
        var provider = new FakeVoiceProvider
        {
            Stream = _ => throw new VoiceProviderException("stream_failed", retryable: true),
            Synthesize = request => Task.FromResult(new VoiceAudio(new byte[] { 1, 2 }, 24_000, 1)),
        };
        await using var leases = new RuntimeLeaseCoordinator();
        var coordinator = new VoiceStreamCoordinator(provider, leases);

        var output = await CollectAsync(coordinator.StreamAsync(
            new VoiceRequest("Một câu.", "Ru (OmniVoice)", 1.0, "original"),
            CancellationToken.None));

        Assert.Equal(["audio", "ended"], output.Select(chunk => chunk.State));
        Assert.Equal(1, provider.SynthesisCalls);
        Assert.Equal(new byte[] { 1, 2 }, output[0].Audio.ToArray());
    }

    private static async Task<List<VoiceChunk>> CollectAsync(IAsyncEnumerable<VoiceChunk> chunks)
    {
        var output = new List<VoiceChunk>();
        await foreach (var chunk in chunks)
        {
            output.Add(chunk);
        }

        return output;
    }

    private static async IAsyncEnumerable<VoiceChunk> Async(params VoiceChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private sealed class FakeVoiceProvider : IVoiceProvider
    {
        public Func<VoiceRequest, IAsyncEnumerable<VoiceChunk>>? Stream { get; init; }
        public Func<VoiceRequest, Task<VoiceAudio>>? Synthesize { get; init; }
        public int SynthesisCalls { get; private set; }

        public Task<VoiceAudio> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
        {
            SynthesisCalls++;
            return Synthesize?.Invoke(request)
                ?? Task.FromResult(new VoiceAudio(new byte[] { 1 }, 24_000, 1));
        }

        public IAsyncEnumerable<VoiceChunk> StreamAsync(VoiceRequest request, CancellationToken cancellationToken) =>
            Stream?.Invoke(request) ?? Async(new VoiceChunk("ended", 0));
    }
}
