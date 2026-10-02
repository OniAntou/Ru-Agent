using System.Drawing;
using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests.Images;

public sealed class CliClipboardTests
{
    [Fact]
    public void DibToBmp_WrapsA32bppDibGdiCanDecode()
    {
        var dib = Build32bppDib(4, 4);

        var bmp = CliClipboard.DibToBmp(dib);

        Assert.NotNull(bmp);
        using var stream = new MemoryStream(bmp!, writable: false);
        using var image = Image.FromStream(stream);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
    }

    [Fact]
    public void DibToBmp_RejectsRuntPayloads()
    {
        Assert.Null(CliClipboard.DibToBmp([]));
        Assert.Null(CliClipboard.DibToBmp(new byte[20]));
        Assert.Null(CliClipboard.DibToBmp(new byte[40]));
    }

    [Fact]
    public void SaveDibAsTempPng_WritesADecodablePngAndCleansUp()
    {
        var dib = Build32bppDib(6, 3);

        var path = CliClipboard.SaveDibAsTempPng(dib);

        Assert.NotNull(path);
        try
        {
            Assert.True(File.Exists(path));
            using var image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path!)));
            Assert.Equal(6, image.Width);
            Assert.Equal(3, image.Height);
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Fact]
    public void SaveDibAsTempPng_GarbageReturnsNullWithoutLeavingAFile()
    {
        var path = CliClipboard.SaveDibAsTempPng(Garbage());

        Assert.Null(path);
    }

    private static byte[] Garbage()
    {
        var bytes = new byte[128];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Minimal bottom-up 32bpp BI_RGB DIB: 40-byte header plus BGRA pixels.</summary>
    private static byte[] Build32bppDib(int width, int height)
    {
        var stride = width * 4;
        var dib = new byte[40 + (stride * height)];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * height).CopyTo(dib, 20);
        for (var i = 40; i < dib.Length; i += 4)
        {
            dib[i] = 0x40;
            dib[i + 1] = 0x80;
            dib[i + 2] = 0xC0;
            dib[i + 3] = 0xFF;
        }

        return dib;
    }
}
