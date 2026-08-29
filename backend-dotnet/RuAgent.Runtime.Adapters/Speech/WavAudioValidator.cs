using System.Buffers.Binary;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Speech;

public readonly record struct WavAudioFormat(
    int SampleRateHz,
    int Channels,
    int BitsPerSample,
    int DataBytes);

public static class WavAudioValidator
{
    public static WavAudioFormat Validate(
        ReadOnlySpan<byte> wav,
        int expectedRate,
        int expectedChannels)
    {
        if (expectedRate <= 0
            || expectedRate > 384_000
            || expectedChannels <= 0
            || expectedChannels > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRate));
        }

        if (wav.Length < 44 || wav.Length > RuntimeLimits.MaxSpeechAudioBytes)
        {
            throw InvalidAudio();
        }

        if (!wav[..4].SequenceEqual("RIFF"u8)
            || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw InvalidAudio();
        }

        var declaredRiffSize = BinaryPrimitives.ReadInt32LittleEndian(wav[4..8]);
        if (declaredRiffSize < 4 || declaredRiffSize != wav.Length - 8)
        {
            throw InvalidAudio();
        }

        var offset = 12;
        var sawFormat = false;
        var sawData = false;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        short blockAlign = 0;
        int dataBytes = 0;

        while (offset < wav.Length)
        {
            if (wav.Length - offset < 8)
            {
                throw InvalidAudio();
            }

            var chunkId = wav.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            if (chunkSize < 0 || chunkSize > wav.Length - offset - 8)
            {
                throw InvalidAudio();
            }

            var chunk = wav.Slice(offset + 8, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (sawFormat || chunk.Length < 16)
                {
                    throw InvalidAudio();
                }

                sawFormat = true;
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(chunk[12..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (sawData)
                {
                    throw InvalidAudio();
                }

                sawData = true;
                dataBytes = chunk.Length;
            }

            offset += 8 + chunkSize;
            if ((chunkSize & 1) != 0)
            {
                if (offset >= wav.Length)
                {
                    throw InvalidAudio();
                }

                offset++;
            }
        }

        var expectedBlockAlign = checked(expectedChannels * 2);
        if (!sawFormat
            || !sawData
            || audioFormat != 1
            || channels != expectedChannels
            || sampleRate != expectedRate
            || bitsPerSample != 16
            || blockAlign != expectedBlockAlign
            || dataBytes <= 0
            || dataBytes % expectedBlockAlign != 0)
        {
            throw InvalidAudio();
        }

        return new WavAudioFormat(sampleRate, channels, bitsPerSample, dataBytes);
    }

    private static SpeechProviderException InvalidAudio() =>
        new("invalid_audio", retryable: false);
}
