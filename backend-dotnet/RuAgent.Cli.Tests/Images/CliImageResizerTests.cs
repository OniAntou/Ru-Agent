using System.Drawing;
using System.Drawing.Imaging;
using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests.Images;

public sealed class CliImageResizerTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Jpeg(int width, int height, long quality)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.SeaGreen);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    private static byte[] Bmp(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Goldenrod);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        return stream.ToArray();
    }

    [Fact]
    public void ResizeIfNeeded_SmallPngPassesThroughUntouched()
    {
        var bytes = Png(120, 80);

        var result = CliImageResizer.ResizeIfNeeded(bytes);

        Assert.Same(bytes, result.Bytes);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(120, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void ResizeIfNeeded_SmallJpegPassesThroughUntouched()
    {
        var bytes = Jpeg(120, 80, 90);

        var result = CliImageResizer.ResizeIfNeeded(bytes);

        Assert.Same(bytes, result.Bytes);
        Assert.Equal("image/jpeg", result.MimeType);
    }

    [Fact]
    public void ResizeIfNeeded_CapsTheLongestSideAndKeepsTheRatio()
    {
        var bytes = Png(4000, 100);

        var result = CliImageResizer.ResizeIfNeeded(bytes);

        Assert.Equal(2048, result.Width);
        Assert.Equal(51, result.Height);
        Assert.Equal("image/png", result.MimeType);
        Assert.NotSame(bytes, result.Bytes);
    }

    [Fact]
    public void ResizeIfNeeded_ReencodesBmpAsJpegEvenWhenSmall()
    {
        var bytes = Bmp(64, 48);

        var result = CliImageResizer.ResizeIfNeeded(bytes);

        Assert.Equal("image/jpeg", result.MimeType);
        Assert.Equal(64, result.Width);
        Assert.Equal(48, result.Height);
        Assert.NotSame(bytes, result.Bytes);
    }

    [Fact]
    public void ResizeIfNeeded_ReencodesWhenOverTheByteCap()
    {
        var bytes = Jpeg(400, 400, 100);

        var result = CliImageResizer.ResizeIfNeeded(bytes, maxBytes: 100);

        Assert.Equal("image/jpeg", result.MimeType);
        Assert.NotSame(bytes, result.Bytes);
        Assert.Equal(400, result.Width);
        Assert.Equal(400, result.Height);
    }

    [Fact]
    public void ResizeIfNeeded_UnreadableBytesFailClosed()
    {
        var bytes = new byte[256];
        Random.Shared.NextBytes(bytes);

        Assert.Throws<InvalidDataException>(() => CliImageResizer.ResizeIfNeeded(bytes));
    }

    [Fact]
    public void ResizeIfNeeded_WebpUnderBothCapsPassesThroughWithHeaderDimensions()
    {
        var bytes = WebpExtended(320, 200);

        var result = CliImageResizer.ResizeIfNeeded(bytes);

        Assert.Same(bytes, result.Bytes);
        Assert.Equal("image/webp", result.MimeType);
        Assert.Equal(320, result.Width);
        Assert.Equal(200, result.Height);
    }

    [Fact]
    public void ResizeIfNeeded_WebpOverTheDimensionCapFailsClosed()
    {
        var bytes = WebpExtended(4000, 100);

        var exception = Assert.Throws<InvalidDataException>(() => CliImageResizer.ResizeIfNeeded(bytes));

        Assert.Contains("webp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResizeIfNeeded_WebpOverTheByteCapFailsClosed()
    {
        var bytes = WebpExtended(100, 100);

        Assert.Throws<InvalidDataException>(() => CliImageResizer.ResizeIfNeeded(bytes, maxBytes: 16));
    }

    [Fact]
    public void TryReadWebpDimensions_ReadsTheLosslessHeader()
    {
        var bytes = WebpLossless(123, 45);

        Assert.True(CliImageResizer.TryReadWebpDimensions(bytes, out var width, out var height));
        Assert.Equal(123, width);
        Assert.Equal(45, height);
    }

    [Fact]
    public void TryReadWebpDimensions_ReadsTheLossyHeader()
    {
        var bytes = WebpLossy(640, 480);

        Assert.True(CliImageResizer.TryReadWebpDimensions(bytes, out var width, out var height));
        Assert.Equal(640, width);
        Assert.Equal(480, height);
    }

    [Fact]
    public void TryReadWebpDimensions_RejectsABadLossyStartCode()
    {
        var bytes = WebpLossy(640, 480);
        bytes[23] = 0x00;

        Assert.False(CliImageResizer.TryReadWebpDimensions(bytes, out _, out _));
    }

    [Fact]
    public void TryReadWebpDimensions_RejectsNonWebp()
    {
        Assert.False(CliImageResizer.TryReadWebpDimensions(Png(4, 4), out _, out _));
        Assert.False(CliImageResizer.TryReadWebpDimensions([], out _, out _));
    }

    /// <summary>Minimal RIFF/WEBP container with a VP8X header (canvas minus one, 24-bit LE).</summary>
    private static byte[] WebpExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        Write24(bytes, 24, width - 1);
        Write24(bytes, 27, height - 1);
        return bytes;

        static void Write24(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
        }
    }

    /// <summary>Minimal RIFF/WEBP container with a VP8 header (lossy): frame tag, start code, 14-bit sizes.</summary>
    private static byte[] WebpLossy(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8 "u8.CopyTo(bytes.AsSpan(12));
        bytes[23] = 0x9D;
        bytes[24] = 0x01;
        bytes[25] = 0x2A;
        bytes[26] = (byte)(width & 0xFF);
        bytes[27] = (byte)((width >> 8) & 0x3F);
        bytes[28] = (byte)(height & 0xFF);
        bytes[29] = (byte)((height >> 8) & 0x3F);
        return bytes;
    }

    /// <summary>Minimal VP8L header: signature byte then two packed 14-bit values (minus one).</summary>
    private static byte[] WebpLossless(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));
        bytes[20] = 0x2F;
        var packed = ((width - 1) & 0x3FFF) | (((height - 1) & 0x3FFF) << 14);
        bytes[21] = (byte)(packed & 0xFF);
        bytes[22] = (byte)((packed >> 8) & 0xFF);
        bytes[23] = (byte)((packed >> 16) & 0xFF);
        bytes[24] = (byte)((packed >> 24) & 0xFF);
        return bytes;
    }
}
