using System.Buffers.Binary;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Minimal image byte layouts with real format headers. Pixels are not decoded by
/// the Broker, so a header plus filler is enough to exercise classification, size
/// and dimension bounds.
/// </summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height, int totalBytes = 64)
    {
        var bytes = new byte[Math.Max(totalBytes, 33)];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    public static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        // APP0 JFIF segment, then a baseline start-of-frame segment.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    public static byte[] WebpExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 22);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        WriteUInt24(bytes.AsSpan(24), width - 1);
        WriteUInt24(bytes.AsSpan(27), height - 1);
        return bytes;
    }

    private static void WriteUInt24(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }
}
