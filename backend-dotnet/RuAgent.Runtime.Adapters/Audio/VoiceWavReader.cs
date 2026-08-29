using System.Buffers.Binary;

namespace RuAgent.Runtime.Adapters.Audio;

internal static class VoiceWavReader
{
    public static (int SampleRateHz, int Channels) Validate(
        ReadOnlyMemory<byte> wav,
        int maxBytes)
    {
        if (wav.IsEmpty || wav.Length > maxBytes || wav.Length < 44)
        {
            throw new OmniVoiceProviderException("invalid_audio", retryable: false);
        }

        var bytes = wav.Span;
        if (!bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WAVE"u8))
        {
            throw new OmniVoiceProviderException("invalid_audio", retryable: false);
        }

        var declaredRiffSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
        if (declaredRiffSize < 4 || declaredRiffSize > bytes.Length - 8)
        {
            throw new OmniVoiceProviderException("invalid_audio", retryable: false);
        }

        var offset = 12;
        short format = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        var dataBytes = 0;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (chunkSize < 0 || chunkSize > bytes.Length - offset - 8)
            {
                throw new OmniVoiceProviderException("invalid_audio", retryable: false);
            }

            var chunk = bytes.Slice(offset + 8, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    throw new OmniVoiceProviderException("invalid_audio", retryable: false);
                }

                format = BinaryPrimitives.ReadInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataBytes = chunk.Length;
            }

            offset += 8 + chunkSize + (chunkSize & 1);
        }

        if (format != 1 || channels != 1 || sampleRate != 24_000 || bitsPerSample != 16 || dataBytes <= 0 || dataBytes % 2 != 0)
        {
            throw new OmniVoiceProviderException("invalid_audio", retryable: false);
        }

        return (sampleRate, channels);
    }
}
