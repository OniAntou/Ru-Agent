namespace RuAgent.Client.Attachments;

/// <summary>
/// Removes descriptive metadata from an attached JPEG or PNG without re-encoding it.
/// </summary>
/// <remarks>
/// An image small enough to need no resize used to travel byte for byte, so a photo's
/// EXIF (GPS position, camera serial, capture time) reached the provider and the stored
/// payload. Re-encoding would cost quality on screenshots and line art, so metadata
/// segments are dropped losslessly instead: JPEG APP1 (EXIF/XMP), APP13 (IPTC) and
/// comments; PNG <c>eXIf</c>, text and time chunks. Colour data (JFIF, ICC profile,
/// Adobe) is kept. A JPEG whose EXIF rotates the picture is reported through
/// <see cref="JpegOrientation"/> so the caller re-encodes it upright instead: dropping
/// the tag alone would turn the photo on its side.
/// </remarks>
public static class ImageMetadata
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The bytes without metadata. The same array comes back when there was nothing to
    /// drop, and when the input is not a well-formed JPEG or PNG (the decoder then gets
    /// the original and decides).
    /// </summary>
    public static byte[] Strip(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (IsJpeg(image)) return StripJpeg(image) ?? image;
        if (image.AsSpan().StartsWith(PngSignature)) return StripPng(image) ?? image;
        return image;
    }

    public static bool IsJpeg(ReadOnlySpan<byte> image) => image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8;

    /// <summary>The EXIF orientation of a JPEG (1 = upright), or 1 when it carries none.</summary>
    public static int JpegOrientation(ReadOnlySpan<byte> image)
    {
        if (!IsJpeg(image)) return 1;
        var position = 2;
        while (position + 4 <= image.Length && image[position] == 0xFF)
        {
            var marker = image[position + 1];
            if (marker is 0xDA or 0xD9) break;
            var length = (image[position + 2] << 8) | image[position + 3];
            if (length < 2 || position + 2 + length > image.Length) break;
            var segment = image.Slice(position + 4, length - 2);
            if (marker == 0xE1 && segment.StartsWith("Exif\0\0"u8)
                && ReadOrientation(segment[6..]) is { } orientation)
            {
                return orientation;
            }

            position += 2 + length;
        }

        return 1;
    }

    private static byte[]? StripJpeg(byte[] image)
    {
        using var output = new MemoryStream(image.Length);
        output.Write(image, 0, 2);
        var position = 2;
        var dropped = false;
        while (position + 4 <= image.Length)
        {
            if (image[position] != 0xFF) return null;
            var marker = image[position + 1];
            if (marker == 0xDA)
            {
                // Start of scan: the entropy-coded image follows; copy the rest as is.
                if (!dropped) return image;
                output.Write(image, position, image.Length - position);
                return output.ToArray();
            }

            if (marker is 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01)
            {
                output.Write(image, position, 2);
                position += 2;
                continue;
            }

            var length = (image[position + 2] << 8) | image[position + 3];
            if (length < 2 || position + 2 + length > image.Length) return null;
            var drop = marker is 0xE1 or 0xED or 0xFE;
            dropped |= drop;
            if (!drop) output.Write(image, position, 2 + length);
            position += 2 + length;
        }

        return null;
    }

    private static byte[]? StripPng(byte[] image)
    {
        using var output = new MemoryStream(image.Length);
        output.Write(PngSignature);
        var position = PngSignature.Length;
        var dropped = false;
        while (position + 12 <= image.Length)
        {
            var length = (long)((uint)image[position] << 24 | (uint)image[position + 1] << 16
                | (uint)image[position + 2] << 8 | image[position + 3]);
            if (position + 12 + length > image.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(image, position + 4, 4);
            var total = (int)(12 + length);
            if (type is not ("eXIf" or "tEXt" or "zTXt" or "iTXt" or "tIME"))
                output.Write(image, position, total);
            else
                dropped = true;
            position += total;
            if (type == "IEND") return dropped ? output.ToArray() : image;
        }

        return null;
    }

    /// <summary>Tag 0x0112 of IFD0 in a TIFF block, or null when absent or malformed.</summary>
    private static int? ReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == 0x49 && tiff[1] == 0x49) little = true;
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D) little = false;
        else return null;

        var ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd + 2 > tiff.Length) return null;
        var count = U16(tiff, (int)ifd, little);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return null;
            if (U16(tiff, entry, little) == 0x0112)
            {
                var value = U16(tiff, entry + 8, little);
                return value is >= 1 and <= 8 ? value : null;
            }
        }

        return null;
    }

    private static int U16(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? b[at] | b[at + 1] << 8 : b[at] << 8 | b[at + 1];

    private static long U32(ReadOnlySpan<byte> b, int at, bool little) => little
        ? (uint)b[at] | (uint)b[at + 1] << 8 | (uint)b[at + 2] << 16 | (uint)b[at + 3] << 24
        : (uint)b[at] << 24 | (uint)b[at + 1] << 16 | (uint)b[at + 2] << 8 | (uint)b[at + 3];
}
