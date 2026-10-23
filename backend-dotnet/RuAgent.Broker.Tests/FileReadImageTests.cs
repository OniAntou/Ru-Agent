using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>ADR 0026: <c>read</c> returns a workspace image to a model that accepts image input.</summary>
public sealed class FileReadImageTests : IDisposable
{
    private readonly string _root;
    private readonly FileReadAdapter _adapter;

    public FileReadImageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-read-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _adapter = new FileReadAdapter(_root);
    }

    [Fact]
    public async Task Png_is_returned_beside_small_metadata_when_the_model_accepts_images()
    {
        var bytes = TestImages.Png(640, 480, totalBytes: 2048);
        File.WriteAllBytes(FullPath("shot.png"), bytes);

        var result = await _adapter.ExecuteAsync(Args("shot.png"), imageInputAccepted: true, CancellationToken.None);

        Assert.NotNull(result.Image);
        Assert.Equal("image/png", result.Image!.MediaType);
        Assert.Equal(bytes, result.Image.Bytes.ToArray());
        Assert.Equal("shot.png", result.Output["path"]!.GetValue<string>());
        Assert.Equal("image/png", result.Output["media_type"]!.GetValue<string>());
        Assert.Equal(640, result.Output["width"]!.GetValue<int>());
        Assert.Equal(480, result.Output["height"]!.GetValue<int>());
        Assert.Equal(bytes.Length, result.Output["total_bytes"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Output["sha256"]!.GetValue<string>());
        // The bytes never enter the JSON output.
        Assert.False(result.Output.ContainsKey("content"));
        Assert.True(Encoding.UTF8.GetByteCount(result.Output.ToJsonString()) < 512);
    }

    public static TheoryData<string, byte[], string> SupportedFormats => new()
    {
        { "photo.JPG", TestImages.Jpeg(1024, 768), "image/jpeg" },
        { "photo.jpeg", TestImages.Jpeg(1024, 768), "image/jpeg" },
        { "art.webp", TestImages.WebpExtended(1024, 768), "image/webp" },
    };

    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public async Task Media_type_and_size_come_from_the_bytes(string name, byte[] bytes, string mediaType)
    {
        File.WriteAllBytes(FullPath(name), bytes);

        var result = await _adapter.ExecuteAsync(Args(name), imageInputAccepted: true, CancellationToken.None);

        Assert.Equal(mediaType, result.Image!.MediaType);
        Assert.Equal(1024, result.Output["width"]!.GetValue<int>());
        Assert.Equal(768, result.Output["height"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_jpeg_named_png_is_sent_with_its_real_media_type()
    {
        File.WriteAllBytes(FullPath("misnamed.png"), TestImages.Jpeg(10, 10));

        var result = await _adapter.ExecuteAsync(Args("misnamed.png"), imageInputAccepted: true, CancellationToken.None);

        Assert.Equal("image/jpeg", result.Image!.MediaType);
    }

    [Fact]
    public async Task Image_for_a_model_without_image_input_fails_by_name_and_returns_no_bytes()
    {
        File.WriteAllBytes(FullPath("shot.png"), TestImages.Png(10, 10));

        var error = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(Args("shot.png"), imageInputAccepted: false, CancellationToken.None));

        Assert.Equal("image_unsupported_by_model", error.Code);
        // The text-only entry point never falls back to returning image bytes as text.
        var textOnly = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(Args("shot.png"), CancellationToken.None));
        Assert.Equal("image_unsupported_by_model", textOnly.Code);
    }

    [Fact]
    public async Task Image_over_four_mebibytes_fails_before_it_is_returned()
    {
        File.WriteAllBytes(FullPath("big.png"), TestImages.Png(100, 100, ToolOutputImage.MaximumBytes + 1));

        var error = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(Args("big.png"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("image_too_large", error.Code);
    }

    [Fact]
    public async Task Image_at_exactly_four_mebibytes_is_returned()
    {
        File.WriteAllBytes(FullPath("edge.png"), TestImages.Png(100, 100, ToolOutputImage.MaximumBytes));

        var result = await _adapter.ExecuteAsync(Args("edge.png"), imageInputAccepted: true, CancellationToken.None);

        Assert.Equal(ToolOutputImage.MaximumBytes, result.Image!.Bytes.Length);
    }

    [Fact]
    public async Task Image_wider_than_the_provider_bound_fails_by_name()
    {
        File.WriteAllBytes(FullPath("wide.png"), TestImages.Png(ToolOutputImage.MaximumDimension + 1, 10));

        var error = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(Args("wide.png"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("image_dimensions_too_large", error.Code);
    }

    [Fact]
    public async Task Image_extension_with_text_bytes_fails_as_an_invalid_image()
    {
        File.WriteAllText(FullPath("notes.png"), "not an image");

        var error = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(Args("notes.png"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("image_format_invalid", error.Code);
    }

    [Fact]
    public async Task Image_read_with_an_offset_fails_by_name()
    {
        File.WriteAllBytes(FullPath("shot.png"), TestImages.Png(10, 10));
        var arguments = Args("shot.png");
        arguments["offset"] = 8;

        var error = await Assert.ThrowsAsync<FileReadImageException>(async () =>
            await _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("image_offset_unsupported", error.Code);
    }

    [Fact]
    public async Task Image_bytes_without_an_image_extension_keep_the_text_path()
    {
        File.WriteAllBytes(FullPath("shot.bin"), TestImages.Png(10, 10));

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _adapter.ExecuteAsync(Args("shot.bin"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("invalid_utf8", error.Message);
    }

    [Fact]
    public async Task Text_files_are_unchanged_when_images_are_accepted()
    {
        File.WriteAllText(FullPath("readme.txt"), "hello");

        var result = await _adapter.ExecuteAsync(Args("readme.txt"), imageInputAccepted: true, CancellationToken.None);

        Assert.Null(result.Image);
        Assert.Equal("hello", result.Output["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Workspace_path_checks_run_before_image_classification()
    {
        var outside = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await _adapter.ExecuteAsync(Args(Path.Combine("..", "x.png")), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("outside_workspace", outside.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonObject Args(string path) => new() { ["path"] = path };

    private string FullPath(string relative) => Path.Combine(_root, relative);
}
