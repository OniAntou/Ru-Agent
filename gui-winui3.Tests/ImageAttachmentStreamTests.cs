using RuApp.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace RuApp.Tests;

public sealed class ImageAttachmentStreamTests
{
    [Fact]
    public async Task Managed_image_bytes_can_be_prepared_like_picker_drop_and_clipboard_intake()
    {
        byte[] source = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var bytes = new MemoryStream(source, writable: false);
        using var input = bytes.AsRandomAccessStream();

        var result = await ImageAttachmentResizer.ResizeIfNeededAsync(input, "image/png");

        Assert.Equal(source, result.Bytes);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(1u, result.Width);
        Assert.Equal(1u, result.Height);
    }

    [Theory]
    [InlineData("png", 2048, 2)]
    [InlineData("png", 1, 1)]
    [InlineData("jpeg", 2048, 2)]
    [InlineData("jpeg", 1, 1)]
    [InlineData("bmp", 2048, 2)]
    public async Task Managed_image_streams_support_passthrough_resize_and_wire_normalization(
        string format, int maximumDimension, int expectedDimension)
    {
        using var encoded = new InMemoryRandomAccessStream();
        Guid codec = format switch
        {
            "png" => BitmapEncoder.PngEncoderId,
            "jpeg" => BitmapEncoder.JpegEncoderId,
            _ => BitmapEncoder.BmpEncoderId,
        };
        var encoder = await BitmapEncoder.CreateAsync(codec, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 2, 96, 96,
            [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255]);
        await encoder.FlushAsync();
        encoded.Seek(0);
        using var source = new MemoryStream();
        await encoded.AsStreamForRead().CopyToAsync(source);
        source.Position = 0;
        using var input = source.AsRandomAccessStream();

        var result = await ImageAttachmentResizer.ResizeIfNeededAsync(
            input, format == "png" ? "image/png" : "image/jpeg", maximumDimension);

        Assert.Equal((uint)expectedDimension, result.Width);
        Assert.Equal((uint)expectedDimension, result.Height);
        Assert.Contains(result.MimeType, new[] { "image/png", "image/jpeg" });
        Assert.InRange(result.Bytes.Length, 1, ImageAttachmentResizer.DefaultMaxBytes);
        using var normalized = new MemoryStream(result.Bytes, writable: false);
        using var normalizedInput = normalized.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(normalizedInput);
        Assert.Equal(result.Width, decoder.PixelWidth);
        Assert.Equal(result.Height, decoder.PixelHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Invalid_managed_image_streams_fail_as_unsupported_input(int length)
    {
        using var source = new MemoryStream(new byte[length], writable: false);
        using var input = source.AsRandomAccessStream();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageAttachmentResizer.ResizeIfNeededAsync(input, "image/png"));

        Assert.Equal("Image format is not supported.", error.Message);
        Assert.True(source.CanRead);
    }
}
