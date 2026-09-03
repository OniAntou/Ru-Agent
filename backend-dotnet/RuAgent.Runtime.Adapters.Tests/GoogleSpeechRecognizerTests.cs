using System.Buffers.Binary;
using System.Text;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class GoogleSpeechRecognizerTests
{
    [Fact]
    public void Wav_validator_accepts_16_khz_mono_pcm_and_rejects_invalid_shapes()
    {
        var wav = ValidWav(16_000, 1, 16, new byte[] { 0, 0 });

        var format = WavAudioValidator.Validate(wav, 16_000, 1);

        Assert.Equal(16_000, format.SampleRateHz);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(2, format.DataBytes);
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            EncodingBytes("not-wav"),
            16_000,
            1));
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            ValidWav(24_000, 1, 16, new byte[] { 0, 0 }),
            16_000,
            1));
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            ValidWav(16_000, 2, 16, new byte[] { 0, 0, 0, 0 }),
            16_000,
            1));
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            ValidWav(16_000, 1, 8, new byte[] { 0 }),
            16_000,
            1));
        var malformedSize = ValidWav(16_000, 1, 16, new byte[] { 0, 0 });
        BinaryPrimitives.WriteInt32LittleEndian(malformedSize.AsSpan(4), malformedSize.Length);
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            malformedSize,
            16_000,
            1));
    }

    [Fact]
    public void Wav_validator_rejects_empty_odd_or_oversized_audio()
    {
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            ReadOnlySpan<byte>.Empty,
            16_000,
            1));
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            ValidWav(16_000, 1, 16, new byte[] { 0 }),
            16_000,
            1));

        var oversized = new byte[RuntimeLimits.MaxSpeechAudioBytes + 1];
        Assert.Throws<SpeechProviderException>(() => WavAudioValidator.Validate(
            oversized,
            16_000,
            1));
    }

    [Fact]
    public async Task Recognizer_normalizes_transcript_and_forces_vi_vn()
    {
        var transport = new FakeTransport { Transcript = "  Xin   chào\n anh  " };
        var recognizer = new GoogleSpeechRecognizer(transport);

        var text = await recognizer.RecognizeAsync(
            new SpeechRequest(ValidWav(16_000, 1, 16, new byte[] { 0, 0 }), 16_000, 1, "vi-VN"),
            CancellationToken.None);

        Assert.Equal("Xin chào anh", text);
        Assert.Equal("vi-VN", transport.Language);
    }

    [Fact]
    public async Task Recognizer_maps_no_match_timeout_and_transport_errors_without_provider_text()
    {
        var audio = new SpeechRequest(
            ValidWav(16_000, 1, 16, new byte[] { 0, 0 }),
            16_000,
            1,
            "vi-VN");

        foreach (var failure in new[]
        {
            (SpeechTransportFailure.NoMatch, "speech_no_match", false),
            (SpeechTransportFailure.Timeout, "speech_timeout", true),
            (SpeechTransportFailure.Unavailable, "speech_unavailable", true),
            (SpeechTransportFailure.Failed, "speech_failed", true),
        })
        {
            var recognizer = new GoogleSpeechRecognizer(new FakeTransport { Failure = failure.Item1 });
            var error = await Assert.ThrowsAsync<SpeechProviderException>(() => recognizer.RecognizeAsync(
                audio,
                CancellationToken.None));
            Assert.Equal(failure.Item2, error.Code);
            Assert.Equal(failure.Item3, error.Retryable);
            Assert.DoesNotContain("provider secret", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Recognizer_maps_empty_transcript_to_no_match()
    {
        var recognizer = new GoogleSpeechRecognizer(new FakeTransport { Transcript = " \n\t" });

        var error = await Assert.ThrowsAsync<SpeechProviderException>(() => recognizer.RecognizeAsync(
            new SpeechRequest(ValidWav(16_000, 1, 16, new byte[] { 0, 0 }), 16_000, 1, "vi-VN"),
            CancellationToken.None));

        Assert.Equal("speech_no_match", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Recognizer_rejects_an_unbounded_provider_transcript_before_split()
    {
        var recognizer = new GoogleSpeechRecognizer(
            new FakeTransport { Transcript = new string('x', 64 * 1024 + 1) });

        var error = await Assert.ThrowsAsync<SpeechProviderException>(() => recognizer.RecognizeAsync(
            new SpeechRequest(ValidWav(16_000, 1, 16, new byte[] { 0, 0 }), 16_000, 1, "vi-VN"),
            CancellationToken.None));

        Assert.Equal("speech_response_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Recognizer_applies_fifteen_second_timeout_and_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeTransport
        {
            Block = true,
        };
        var recognizer = new GoogleSpeechRecognizer(transport);
        var task = recognizer.RecognizeAsync(
            new SpeechRequest(ValidWav(16_000, 1, 16, new byte[] { 0, 0 }), 16_000, 1, "vi-VN"),
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static byte[] ValidWav(int sampleRate, short channels, short bitsPerSample, byte[] data)
    {
        var blockAlign = checked((short)(channels * (bitsPerSample / 8)));
        var byteRate = checked(sampleRate * blockAlign);
        var bytes = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data.Length);
        data.CopyTo(bytes, 44);
        return bytes;
    }

    private static byte[] EncodingBytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private sealed class FakeTransport : IGoogleSpeechTransport
    {
        public string? Transcript { get; init; }
        public SpeechTransportFailure? Failure { get; init; }
        public bool Block { get; init; }
        public string? Language { get; private set; }

        public async Task<string?> RecognizeAsync(
            ReadOnlyMemory<byte> wavBytes,
            string language,
            CancellationToken cancellationToken)
        {
            Language = language;
            if (Block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Failure is { } failure)
            {
                throw new GoogleSpeechTransportException(failure, "provider secret body");
            }

            return Transcript;
        }
    }
}
