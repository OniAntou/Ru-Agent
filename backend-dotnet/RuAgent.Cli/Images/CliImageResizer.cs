using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RuAgent.Cli.Images;

/// <summary>Result of the resize pass: the bytes and type that actually travel.</summary>
public sealed record CliResizedImage(byte[] Bytes, string MimeType, int Width, int Height);

/// <summary>
/// Enforces the backend's per-image limits before an attachment is built, ported from the
/// GUI <c>ImageAttachmentResizer</c>: cap the longest side at 2048, re-encode anything over
/// the byte cap or in a non-wire format (BMP), keep PNG alpha as PNG and everything else as
/// JPEG q85. WebP has no GDI+ codec, so it passes through only when the header proves it is
/// already within both caps.
/// </summary>
public static class CliImageResizer
{
    public const int DefaultMaxDimension = 2048;
    public const int DefaultMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Largest source canvas accepted before GDI+ decodes it. A small compressed file can
    /// claim a multi-gigapixel canvas; scaling happens after the source decode, so the
    /// cap has to be checked from the container header first. 64 MP covers 8K frames and
    /// ordinary camera photos while keeping the worst-case RGBA surface near 256 MB.
    /// </summary>
    public const long MaxSourcePixels = 64_000_000;

    public static CliResizedImage ResizeIfNeeded(
        byte[] bytes,
        int maxDimension = DefaultMaxDimension,
        int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (TryReadDimensions(bytes, out var sourceWidth, out var sourceHeight)
            && (long)sourceWidth * sourceHeight > MaxSourcePixels)
        {
            throw new InvalidDataException(
                $"image is {sourceWidth}x{sourceHeight}; the source pixel limit is {MaxSourcePixels / 1_000_000} MP");
        }

        if (IsWebp(bytes))
        {
            return PassThroughWebp(bytes, maxDimension, maxBytes);
        }

        using var source = Decode(bytes);
        ApplyExifOrientation(source);

        var sourceIsPng = source.RawFormat.Guid == ImageFormat.Png.Guid;
        var sourceIsJpeg = source.RawFormat.Guid == ImageFormat.Jpeg.Guid;
        var originalWidth = source.Width;
        var originalHeight = source.Height;
        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(originalWidth, originalHeight));
        var needsResize = scale < 0.999;
        var exceedsBytes = bytes.LongLength > maxBytes;
        // BMP/GIF/TIFF are accepted by the picker but have no wire form: the Host and the
        // provider adapters accept only png/jpeg/webp. Passing those bytes through unchanged
        // under a different label would hand the model undecodable bytes, so any non-wire
        // format is re-encoded even when it is small.
        var needsReencode = !(sourceIsPng || sourceIsJpeg);

        if (!needsResize && !exceedsBytes && !needsReencode)
        {
            return new CliResizedImage(bytes, sourceIsPng ? "image/png" : "image/jpeg", originalWidth, originalHeight);
        }

        var targetWidth = Math.Max(1, (int)Math.Round(originalWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(originalHeight * scale));

        using var target = new Bitmap(targetWidth, targetHeight);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        }

        using var output = new MemoryStream();
        if (sourceIsPng && Image.IsAlphaPixelFormat(source.PixelFormat))
        {
            target.Save(output, ImageFormat.Png);
            return new CliResizedImage(output.ToArray(), "image/png", targetWidth, targetHeight);
        }

        SaveJpeg(target, output);
        return new CliResizedImage(output.ToArray(), "image/jpeg", targetWidth, targetHeight);
    }

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length >= 12
        && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
        && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';

    /// <summary>
    /// Reads the canvas size from the container header without decoding pixels. PNG, JPEG,
    /// BMP and WebP are the formats the picker accepts; an unreadable header is left for
    /// the decoder to reject with its own bounded reason.
    /// </summary>
    public static bool TryReadDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (TryReadPngDimensions(bytes, out width, out height))
        {
            return true;
        }

        if (IsWebp(bytes))
        {
            return TryReadWebpDimensions(bytes, out width, out height);
        }

        return TryReadJpegDimensions(bytes, out width, out height)
            || TryReadBmpDimensions(bytes, out width, out height);
    }

    private static bool TryReadPngDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24
            || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47
            || bytes[4] != 0x0D || bytes[5] != 0x0A || bytes[6] != 0x1A || bytes[7] != 0x0A
            || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }

        var index = 2;
        while (index + 3 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = bytes[index + 1];
            index += 2;
            if (marker is 0xD8 or 0xD9)
            {
                continue;
            }

            if (marker == 0xDA)
            {
                // Start of scan: the frame header must already have appeared.
                return false;
            }

            if (marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (index + 1 >= bytes.Length)
            {
                return false;
            }

            var length = (bytes[index] << 8) | bytes[index + 1];
            if (length < 2 || index + length > bytes.Length)
            {
                return false;
            }

            if (IsStartOfFrame(marker) && length >= 7)
            {
                height = (bytes[index + 3] << 8) | bytes[index + 4];
                width = (bytes[index + 5] << 8) | bytes[index + 6];
                return width > 0 && height > 0;
            }

            index += length;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    private static bool TryReadBmpDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 26 || bytes[0] != 'B' || bytes[1] != 'M')
        {
            return false;
        }

        var headerSize = BitConverter.ToInt32(bytes, 14);
        if (headerSize == 12)
        {
            width = BitConverter.ToUInt16(bytes, 18);
            height = BitConverter.ToUInt16(bytes, 20);
        }
        else if (headerSize >= 40)
        {
            width = BitConverter.ToInt32(bytes, 18);
            // A negative height means a top-down bitmap; the canvas is still |height|.
            var rawHeight = BitConverter.ToInt32(bytes, 22);
            height = rawHeight == int.MinValue ? 0 : Math.Abs(rawHeight);
        }

        return width > 0 && height > 0;
    }

    private static CliResizedImage PassThroughWebp(byte[] bytes, int maxDimension, int maxBytes)
    {
        if (!TryReadWebpDimensions(bytes, out var width, out var height))
        {
            throw new InvalidDataException("the webp header could not be read");
        }

        if (bytes.LongLength > maxBytes || Math.Max(width, height) > maxDimension)
        {
            // No WebP codec on this stack: an oversized WebP cannot be resized locally, so
            // it fails closed with a bounded reason instead of shipping bytes the backend
            // will reject after the upload.
            throw new InvalidDataException(
                $"webp is over the limit ({width}x{height}, {bytes.Length}B); keep it under {maxDimension}px and {CliImageAttachment.FormatBytes(maxBytes)}");
        }

        return new CliResizedImage(bytes, "image/webp", width, height);
    }

    private static void SaveJpeg(Image image, Stream output)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        image.Save(output, encoder, parameters);
    }

    private static Image Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            throw new InvalidDataException("the file is not a readable image", ex);
        }
    }

    private static void ApplyExifOrientation(Image image)
    {
        const int ExifOrientationId = 0x0112;
        if (!image.PropertyIdList.Contains(ExifOrientationId))
        {
            return;
        }

        var item = image.GetPropertyItem(ExifOrientationId);
        var value = item?.Value is { Length: > 0 } data ? data[0] : (byte)1;
        var rotateFlip = value switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
        {
            image.RotateFlip(rotateFlip);
        }

        image.RemovePropertyItem(ExifOrientationId);
    }

    /// <summary>
    /// Reads the canvas dimensions from a WebP container without a codec: RIFF/WEBP then
    /// the VP8 (lossy), VP8L (lossless) or VP8X (extended) frame header.
    /// </summary>
    public static bool TryReadWebpDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 30
            || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'E' || bytes[10] != 'B' || bytes[11] != 'P')
        {
            return false;
        }

        var chunk = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
        switch (chunk)
        {
            case "VP8X":
                width = 1 + (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16));
                height = 1 + (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16));
                return true;
            case "VP8L":
                // Signature byte 0x2F followed by two packed 14-bit values (minus one).
                if (bytes[20] != 0x2F)
                {
                    return false;
                }

                var packed = bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24);
                width = 1 + (packed & 0x3FFF);
                height = 1 + ((packed >> 14) & 0x3FFF);
                return true;
            case "VP8 ":
                // Lossy keyframe: 3-byte frame tag, 3-byte start code, then 16-bit sizes.
                if (bytes[23] != 0x9D || bytes[24] != 0x01 || bytes[25] != 0x2A)
                {
                    return false;
                }

                width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
                height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
                return true;
            default:
                return false;
        }
    }
}
