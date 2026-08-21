using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RuApp.Services;

public static class PcmWavEncoder
{
    private const int HeaderSize = 44;
    private const short BitsPerSample = 16;

    public static byte[] Encode(IReadOnlyList<short> samples, int sampleRate, short channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one PCM sample is required.", nameof(samples));
        }
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8000 and 192000 Hz.");
        }
        if (channels != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Voice turns must be mono.");
        }

        int dataSize = checked(samples.Count * sizeof(short));
        int totalSize = checked(HeaderSize + dataSize);
        byte[] wav = new byte[totalSize];
        Span<byte> header = wav.AsSpan(0, HeaderSize);

        "RIFF"u8.CopyTo(header[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], checked((uint)(36 + dataSize)));
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..32],
            checked((uint)(sampleRate * channels * (BitsPerSample / 8))));
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[32..34],
            checked((ushort)(channels * (BitsPerSample / 8))));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], checked((ushort)BitsPerSample));
        "data"u8.CopyTo(header[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], checked((uint)dataSize));

        Span<byte> payload = wav.AsSpan(HeaderSize);
        for (int index = 0; index < samples.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.Slice(index * sizeof(short), sizeof(short)), samples[index]);
        }

        return wav;
    }
}
