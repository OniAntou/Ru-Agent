using System.Buffers.Binary;
using System.IO.Compression;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>
/// Downscales a top-down BGRA capture and encodes it as an 8-bit RGB PNG. The
/// Broker has no image library (ADR 0026), so this is a small box filter and a
/// zlib-backed encoder.
/// </summary>
public static class ScreenshotEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodePng(byte[] bgra, int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0
            || width > sourceWidth || height > sourceHeight || bgra.Length < sourceWidth * sourceHeight * 4)
            throw new ArgumentException("Invalid screenshot dimensions.");

        // Each row: one filter byte (0 = none) then RGB.
        var raw = new byte[height * (width * 3 + 1)];
        var output = 0;
        for (var y = 0; y < height; y++)
        {
            raw[output++] = 0;
            var y0 = (int)((long)y * sourceHeight / height);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)((long)x * sourceWidth / width);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / width));
                long blue = 0, green = 0, red = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * sourceWidth * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var offset = row + sx * 4;
                        blue += bgra[offset];
                        green += bgra[offset + 1];
                        red += bgra[offset + 2];
                    }
                }

                var count = (y1 - y0) * (x1 - x0);
                raw[output++] = (byte)(red / count);
                raw[output++] = (byte)(green / count);
                raw[output++] = (byte)(blue / count);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour RGB
        WriteChunk(png, "IHDR"u8, header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(png, "IDAT"u8, compressed.ToArray());
        }

        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, type);
        crc = Update(crc, data);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc ^ 0xFFFFFFFFu);
        stream.Write(trailer);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
