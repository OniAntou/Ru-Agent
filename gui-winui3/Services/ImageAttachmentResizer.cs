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

    public static async Task<(byte[] Bytes, string MimeType, uint Width, uint Height)> ResizeIfNeededAsync(
        IRandomAccessStream inputStream,
        string mimeType,
        int maxDimension = DefaultMaxDimension,
        int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(inputStream);

        inputStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        uint origWidth = decoder.PixelWidth;
        uint origHeight = decoder.PixelHeight;

        double scale = Math.Min(1.0, (double)maxDimension / Math.Max(origWidth, origHeight));
        bool needsResize = scale < 0.999;
        bool exceedsBytes = (long)inputStream.Size > maxBytes;

        if (!needsResize && !exceedsBytes)
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

        if (mimeType == "image/png" && hasAlpha)
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

        outputStream.Seek(0);
        using var outNetStream = outputStream.AsStreamForRead();
        using var resultMs = new MemoryStream((int)outputStream.Size);
        await outNetStream.CopyToAsync(resultMs).ConfigureAwait(false);

        return (resultMs.ToArray(), outMime, targetWidth, targetHeight);
    }
}
