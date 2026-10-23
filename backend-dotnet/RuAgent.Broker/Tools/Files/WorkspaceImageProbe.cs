using System.Buffers.Binary;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Classifies a workspace file as a PNG, JPEG or WebP image (ADR 0026). A file is
/// an image only when its extension and its leading bytes agree; the media type
/// comes from the bytes, because providers reject a type that does not match the
/// data. The pixel size is read from the format header without decoding pixels.
/// </summary>
internal static class WorkspaceImageProbe
{
    public static bool HasImageExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";

    /// <summary>The media type the bytes declare, or null when they are not a supported image.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> bytes) => ToolOutputImage.SniffMediaType(bytes);

    /// <summary>Width and height from the format header, or null when the header is unreadable.</summary>
    public static (int Width, int Height)? ReadDimensions(string mediaType, ReadOnlySpan<byte> bytes) => mediaType switch
    {
        "image/png" => ReadPng(bytes),
        "image/jpeg" => ReadJpeg(bytes),
        "image/webp" => ReadWebp(bytes),
        _ => null,
    };

    public static bool IsWithinDimensionBound((int Width, int Height) size) =>
        size.Width <= ToolOutputImage.MaximumDimension && size.Height <= ToolOutputImage.MaximumDimension;

    private static (int, int)? ReadPng(ReadOnlySpan<byte> bytes)
    {
        // The first chunk must be IHDR: length(4) "IHDR" width(4) height(4), big-endian.
        if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8)) return null;
        return Positive(
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]));
    }

    private static (int, int)? ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        var position = 2;
        while (position + 1 < bytes.Length)
        {
            if (bytes[position] != 0xFF) return null;
            var marker = bytes[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            position += 2;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // standalone markers
            if (marker is 0xD9 or 0xDA) return null; // end of image or scan before any frame header
            if (position + 2 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..(position + 2)]);
            if (length < 2 || position + length > bytes.Length) return null;
            // Start-of-frame markers, excluding DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 7) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..(position + 5)]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..(position + 7)]);
                return Positive(width, height);
            }

            position += length;
        }

        return null;
    }

    private static (int, int)? ReadWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30) return null;
        var chunk = bytes[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Canvas width-1 and height-1 as 24-bit little-endian values.
            var width = bytes[24] | (bytes[25] << 8) | (bytes[26] << 16);
            var height = bytes[27] | (bytes[28] << 8) | (bytes[29] << 16);
            return Positive((uint)width + 1, (uint)height + 1);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (bytes[20] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
            return Positive((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Lossy key frame: 3-byte frame tag, start code 9D 01 2A, then 14-bit sizes.
            if (bytes[23] != 0x9D || bytes[24] != 0x01 || bytes[25] != 0x2A) return null;
            return Positive(
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3FFF),
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3FFF));
        }

        return null;
    }

    private static (int, int)? Positive(uint width, uint height) =>
        width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ? null : ((int)width, (int)height);
}
