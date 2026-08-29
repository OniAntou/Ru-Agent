using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;

namespace RuAgent.Runtime.Adapters.Audio;

public sealed class VoiceStreamCoordinator
{
    private readonly IVoiceProvider _provider;
    private readonly RuntimeLeaseCoordinator _leases;

    public VoiceStreamCoordinator(
        IVoiceProvider provider,
        RuntimeLeaseCoordinator leases)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    public async IAsyncEnumerable<VoiceChunk> StreamAsync(
        VoiceRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cleanText = VoiceChunker.Clean(request.Text);
        var chunks = VoiceChunker.Split(cleanText);
        if (chunks.Count == 0)
        {
            throw new VoiceProviderException("invalid_request", retryable: false);
        }

        await using var lease = await _leases
            .AcquireAsync(ResourceKind.GpuAudio, cancellationToken)
            .ConfigureAwait(false);
        var sawAudio = false;
        var nextSequence = 0L;
        var normalizedRequest = new VoiceRequest(
            cleanText,
            request.VoiceProfile,
            request.Speed,
            request.VoiceStyleId);
        var stream = await ConsumeStreamAsync(
            normalizedRequest,
            cancellationToken).ConfigureAwait(false);
        foreach (var chunk in stream.Chunks)
        {
            if (chunk.State == "audio")
            {
                sawAudio = true;
                yield return new VoiceChunk("audio", nextSequence++, chunk.Audio);
                continue;
            }

            if (chunk.State is "cancelled" or "failed")
            {
                throw new VoiceProviderException(
                    chunk.State == "cancelled" ? "cancelled" : "voice_failed",
                    retryable: chunk.State == "failed");
            }
        }

        if (stream.Error is not null && sawAudio)
        {
            throw stream.Error;
        }

        if (!sawAudio)
        {
            var fallback = await SynthesizeFallbackAsync(
                chunks,
                request,
                cancellationToken).ConfigureAwait(false);
            foreach (var audio in fallback.Audio)
            {
                yield return new VoiceChunk("audio", nextSequence++, audio.WavBytes);
                sawAudio = true;
            }

            if (fallback.Error is not null)
            {
                throw stream.Error ?? new VoiceProviderException("voice_failed", retryable: true, fallback.Error);
            }
        }

        if (!sawAudio)
        {
            throw stream.Error ?? new VoiceProviderException("voice_empty_audio", retryable: true);
        }

        yield return new VoiceChunk("ended", nextSequence);
    }

    private async Task<FallbackCapture> SynthesizeFallbackAsync(
        IReadOnlyList<string> chunks,
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        var audio = new List<VoiceAudio>();
        try
        {
            foreach (var textChunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audio.Add(await _provider.SynthesizeAsync(
                    new VoiceRequest(
                        textChunk,
                        request.VoiceProfile,
                        request.Speed,
                        request.VoiceStyleId),
                    cancellationToken).ConfigureAwait(false));
            }

            return new FallbackCapture(audio, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new FallbackCapture(audio, error);
        }
    }

    private async Task<StreamCapture> ConsumeStreamAsync(
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        var chunks = new List<VoiceChunk>();
        try
        {
            await foreach (var chunk in _provider.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }

            return new StreamCapture(chunks, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new StreamCapture(chunks, error);
        }
    }

    private sealed record StreamCapture(
        IReadOnlyList<VoiceChunk> Chunks,
        Exception? Error);

    private sealed record FallbackCapture(
        IReadOnlyList<VoiceAudio> Audio,
        Exception? Error);
}
