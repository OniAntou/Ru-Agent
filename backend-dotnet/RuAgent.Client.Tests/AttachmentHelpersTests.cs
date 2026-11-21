using System.Text;
using RuAgent.Client.Attachments;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AttachmentHelpersTests
{
    [Fact]
    public void Utf8_text_decodes_and_a_bom_is_not_prompt_text()
    {
        Assert.True(TextAttachmentDecoder.TryDecode([.. Encoding.UTF8.GetPreamble(), .. "héllo"u8], out var text, out _));
        Assert.Equal("héllo", text);
        Assert.True(TextAttachmentDecoder.TryDecode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hi")], out text, out _));
        Assert.Equal("hi", text);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x00, 0x49 })]          // NUL: a binary file
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 })]    // Windows-1252 "café": invalid UTF-8
    public void Binary_or_legacy_encoded_files_are_refused_by_name(byte[] bytes)
    {
        Assert.False(TextAttachmentDecoder.TryDecode(bytes, out var text, out var code));
        Assert.Empty(text);
        Assert.Equal(TextAttachmentDecoder.NotTextCode, code);
    }

    [Fact]
    public void Jpeg_exif_and_comments_are_dropped_while_colour_and_image_data_stay()
    {
        var jfif = Segment(0xE0, "JFIF\0"u8.ToArray());
        var exif = Segment(0xE1, [.. "Exif\0\0"u8, .. Tiff(orientation: 1)]);
        var icc = Segment(0xE2, "ICC_PROFILE\0"u8.ToArray());
        var comment = Segment(0xFE, "secret"u8.ToArray());
        byte[] scan = [0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0xFF, 0xD9];
        byte[] image = [0xFF, 0xD8, .. jfif, .. exif, .. icc, .. comment, .. scan];

        var stripped = ImageMetadata.Strip(image);

        Assert.Equal<byte>([0xFF, 0xD8, .. jfif, .. icc, .. scan], stripped);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Jpeg_orientation_is_read_so_a_rotated_photo_is_re_encoded_upright(int orientation)
    {
        byte[] image = [0xFF, 0xD8, .. Segment(0xE1, [.. "Exif\0\0"u8, .. Tiff(orientation)]), 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];
        Assert.Equal(orientation, ImageMetadata.JpegOrientation(image));
    }

    [Fact]
    public void Png_text_and_exif_chunks_are_dropped_and_an_untouched_image_is_returned_as_is()
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var header = Chunk("IHDR", new byte[13]);
        var text = Chunk("tEXt", "Author\0me"u8.ToArray());
        var exif = Chunk("eXIf", new byte[4]);
        var data = Chunk("IDAT", [1, 2, 3]);
        var end = Chunk("IEND", []);

        Assert.Equal<byte>([.. signature, .. header, .. data, .. end],
            ImageMetadata.Strip([.. signature, .. header, .. text, .. exif, .. data, .. end]));
        byte[] clean = [.. signature, .. header, .. data, .. end];
        Assert.Same(clean, ImageMetadata.Strip(clean));
    }

    [Theory]
    [InlineData("Dockerfile", true)]
    [InlineData("app.vue", true)]
    [InlineData("settings.jsonc", true)]
    [InlineData("logo.svg", true)]
    [InlineData("archive.zip", false)]
    public void Both_clients_share_one_text_file_list(string name, bool expected) =>
        Assert.Equal(expected, AttachmentFileTypes.IsTextOrCodeFile(name));

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    /// <summary>A little-endian TIFF block with one IFD0 entry: orientation.</summary>
    private static byte[] Tiff(int orientation) =>
    [
        0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x01, 0x00,
        0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private static byte[] Chunk(string type, byte[] data)
    {
        var length = data.Length;
        return [(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
    }
}
