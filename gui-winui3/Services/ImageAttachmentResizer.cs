using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RuApp.Services;

public static class ImageAttachmentResizer
{
    public const int DefaultMaxDimension = 2048;
    public const int DefaultMaxBytes = 4 * 1024 * 1024; // 4MB backend decoded limit
    public const int DefaultMaxInputBytes = 10 * 1024 * 1024;
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
            return (ms.ToArray(), mimeType, origWidth, origHeight);
        }

        uint targetWidth = Math.Max(1, (uint)Math.Round(origWidth * scale));
        uint targetHeight = Math.Max(1, (uint)Math.Round(origHeight * scale));

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
                ["ImageQuality"] = new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single)
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
        if (outputStream.Size > (ulong)maxBytes)
        {
            throw new InvalidDataException("Normalized image exceeds the decoded-size limit.");
        }

        outputStream.Seek(0);
        using var outNetStream = outputStream.AsStreamForRead();
        using var resultMs = new MemoryStream((int)outputStream.Size);
        await outNetStream.CopyToAsync(resultMs).ConfigureAwait(false);

        return (resultMs.ToArray(), outMime, targetWidth, targetHeight);
    }

    private static async Task<string> DetectMimeTypeAsync(IRandomAccessStream stream)
    {
        byte[] signature = new byte[SignatureBufferLength];
        using var input = stream.GetInputStreamAt(0).AsStreamForRead();
        int totalRead = 0;
        while (totalRead < SignatureBufferLength)
        {
            int read = await input.ReadAsync(signature.AsMemory(totalRead));
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

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
        if (totalRead >= 2 && signature[0] == (byte)'B' && signature[1] == (byte)'M')
        {
            return "image/bmp";
        }

        throw new InvalidDataException("Image format is not supported.");
    }
}
