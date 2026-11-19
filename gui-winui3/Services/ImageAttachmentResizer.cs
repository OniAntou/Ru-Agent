using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RuApp.Services;

public static class ImageAttachmentResizer
{
    public const int DefaultMaxDimension = 2576;
    public const int DefaultMaxBytes = (int)RuAgent.Protocol.Harness.AgentRunLimits.MaxImageBytes; // backend decoded limit

    /// <summary>
    /// Largest source file one image attachment may carry. This bounds the client read
    /// and decode only: the wire image is still normalized to
    /// <see cref="DefaultMaxBytes"/> and <see cref="DefaultMaxDimension"/>, because the
    /// Host, payload store and provider adapters keep their own 4 MiB bound.
    /// </summary>
    public const int DefaultMaxInputBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Bounded source pixel count. The input-byte bound does not bound decoded memory:
    /// a highly compressed image can carry enormous dimensions, and the decoder would
    /// allocate the full surface before the transform runs.
    /// </summary>
    public const int DefaultMaxSourcePixels = 64_000_000;
    private const int SignatureBufferLength = 12;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<(byte[] Bytes, string MimeType, uint Width, uint Height)> ResizeIfNeededAsync(
        IRandomAccessStream inputStream,
        string mimeType,
        int maxDimension = DefaultMaxDimension,
        int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (inputStream.Size > DefaultMaxInputBytes)
        {
            throw new InvalidDataException("Image input exceeds the bounded decode limit.");
        }

        inputStream.Seek(0);
        string sourceMimeType = await DetectMimeTypeAsync(inputStream);
        inputStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        uint origWidth = decoder.PixelWidth;
        uint origHeight = decoder.PixelHeight;
        if (origWidth == 0 || origHeight == 0)
        {
            throw new InvalidDataException("Image dimensions are invalid.");
        }

        if (ExceedsPixelBudget(origWidth, origHeight))
        {
            throw new InvalidDataException("Image dimensions exceed the supported decode limit.");
        }

        double scale = Math.Min(1.0, (double)maxDimension / Math.Max(origWidth, origHeight));
        bool needsResize = scale < 0.999;
        bool exceedsBytes = (long)inputStream.Size > maxBytes;
        // BMP is accepted by the picker but has no wire form: the Host and the
        // provider adapters accept only png/jpeg/webp. Passing BMP bytes through
        // unchanged under a different label would hand the model undecodable
        // bytes, so any non-wire format is re-encoded even when it is small.
        bool needsReencode = mimeType != sourceMimeType;

        if (!needsResize && !exceedsBytes && !needsReencode)
        {
            inputStream.Seek(0);
            using var netStream = inputStream.AsStreamForRead();
            using var ms = new MemoryStream((int)inputStream.Size);
            await netStream.CopyToAsync(ms).ConfigureAwait(false);
            byte[] original = ms.ToArray();
            // An image that needs no resize still loses its EXIF/XMP/text metadata (GPS,
            // camera serial). A JPEG the EXIF rotates is re-encoded upright below instead:
            // dropping only the tag would turn it on its side.
            if (!(RuAgent.Client.Attachments.ImageMetadata.IsJpeg(original)
                && RuAgent.Client.Attachments.ImageMetadata.JpegOrientation(original) != 1))
            {
                return (RuAgent.Client.Attachments.ImageMetadata.Strip(original), mimeType, origWidth, origHeight);
            }
        }

        // A normalized image must fit the wire byte cap. The first attempt keeps the
        // 2576 px long edge at JPEG q0.85 (Anthropic's high-resolution tier); later
        // attempts trade quality, then dimension, rather than failing a valid photo.
        double attemptScale = scale;
        double quality = 0.85;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            uint targetWidth = Math.Max(1, (uint)Math.Round(origWidth * attemptScale));
            uint targetHeight = Math.Max(1, (uint)Math.Round(origHeight * attemptScale));

            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var pixels = pixelData.DetachPixelData();

            Guid encoderId;
            string outMime;
            bool hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;

            if (hasAlpha && sourceMimeType != "image/jpeg")
            {
                encoderId = BitmapEncoder.PngEncoderId;
                outMime = "image/png";
            }
            else
            {
                encoderId = BitmapEncoder.JpegEncoderId;
                outMime = "image/jpeg";
            }

            var outputStream = new InMemoryRandomAccessStream();
            BitmapPropertySet? propertySet = null;
            if (encoderId == BitmapEncoder.JpegEncoderId)
            {
                propertySet = new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single)
                };
            }

            var encoder = propertySet is not null
                ? await BitmapEncoder.CreateAsync(encoderId, outputStream, propertySet)
                : await BitmapEncoder.CreateAsync(encoderId, outputStream);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                targetWidth,
                targetHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels);
            await encoder.FlushAsync();
            if (outputStream.Size <= (ulong)maxBytes)
            {
                outputStream.Seek(0);
                using var outNetStream = outputStream.AsStreamForRead();
                using var resultMs = new MemoryStream((int)outputStream.Size);
                await outNetStream.CopyToAsync(resultMs).ConfigureAwait(false);

                return (resultMs.ToArray(), outMime, targetWidth, targetHeight);
            }

            // PNG with alpha cannot lower JPEG quality, so it can only shrink; JPEG also
            // lowers quality first. Both stay bounded by the attempt count.
            attemptScale *= 0.82;
            quality = Math.Max(0.35, quality - 0.15);
        }

        throw new InvalidDataException("Normalized image exceeds the decoded-size limit.");
    }

    /// <summary>
    /// Whether a source image is over the decode budget. Kept separate from the read
    /// bound so the guard is testable without decoding a crafted image.
    /// </summary>
    internal static bool ExceedsPixelBudget(
        uint width,
        uint height,
        int maxPixels = DefaultMaxSourcePixels) =>
        maxPixels > 0 && (ulong)width * height > (ulong)maxPixels;

    private static async Task<string> DetectMimeTypeAsync(IRandomAccessStream stream)
    {
        byte[] signature;
        using (var reader = new DataReader(stream))
        {
            try
            {
                uint count = await reader.LoadAsync(SignatureBufferLength);
                signature = new byte[count];
                reader.ReadBytes(signature);
            }
            finally
            {
                // MemoryStream.AsRandomAccessStream cannot clone substreams.
                // Read directly and leave the caller's stream open for decoding.
                reader.DetachStream();
            }
        }

        int totalRead = signature.Length;
        if (totalRead >= PngSignature.Length && signature.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return "image/png";
        }
        if (totalRead >= 3 && signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (totalRead >= 12
            && signature.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && signature.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        if (totalRead >= 6
            && (signature.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
                || signature.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (totalRead >= 4
            && (signature.AsSpan(0, 4).SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 })
                || signature.AsSpan(0, 4).SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })))
        {
            return "image/tiff";
        }
        if (totalRead >= 2 && signature[0] == (byte)'B' && signature[1] == (byte)'M')
        {
            return "image/bmp";
        }

        throw new InvalidDataException("Image format is not supported.");
    }
}
