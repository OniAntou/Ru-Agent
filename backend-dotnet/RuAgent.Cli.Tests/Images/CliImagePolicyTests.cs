using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests.Images;

public sealed class CliImagePolicyTests
{
    [Theory]
    [InlineData("cat.png", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("pic.jpeg", true)]
    [InlineData("anim.webp", true)]
    [InlineData("old.bmp", true)]
    [InlineData("notes.txt", false)]
    [InlineData("noext", false)]
    public void IsImageFile_ClassifiesByExtension(string name, bool expected) =>
        Assert.Equal(expected, CliImagePolicy.IsImageFile(name));

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("Main.cs", true)]
    [InlineData("data.json", true)]
    [InlineData("run.ps1", true)]
    [InlineData("cat.png", false)]
    [InlineData("archive.zip", false)]
    public void IsTextOrCodeFile_ClassifiesByExtension(string name, bool expected) =>
        Assert.Equal(expected, CliImagePolicy.IsTextOrCodeFile(name));

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.bmp", "image/jpeg")]
    [InlineData("a.gif", "image/jpeg")]
    public void MimeForExtension_MapsToWireTypes(string name, string expected) =>
        Assert.Equal(expected, CliImagePolicy.MimeForExtension(name));

    [Theory]
    [InlineData("", 0)]
    [InlineData("TWFu", 3)]
    [InlineData("TWE=", 2)]
    [InlineData("TQ==", 1)]
    [InlineData("TWFuTWE=", 5)]
    public void EstimateDecodedBytes_MatchesTheBase64Math(string base64, long expected) =>
        Assert.Equal(expected, CliImagePolicy.EstimateDecodedBytes(base64));

    [Fact]
    public async Task ReadBoundedBytes_ReturnsThePayloadUnderTheCap()
    {
        var payload = new byte[1024];
        using var stream = new MemoryStream(payload);

        var result = await CliImagePolicy.ReadBoundedBytesAsync(stream, maximumBytes: 1024);

        Assert.NotNull(result);
        Assert.Equal(1024, result!.Length);
    }

    [Fact]
    public async Task ReadBoundedBytes_RefusesAPayloadOverTheCap()
    {
        using var stream = new MemoryStream(new byte[1025]);

        var result = await CliImagePolicy.ReadBoundedBytesAsync(stream, maximumBytes: 1024);

        Assert.Null(result);
    }

    [Fact]
    public void TooManyImagesStatus_NamesTheSameNumberTheCheckUses() =>
        Assert.Contains($"Maximum {CliImagePolicy.MaxImageCount} images", CliImagePolicy.TooManyImagesStatus, StringComparison.Ordinal);
}

public sealed class CliImageAttachmentTests
{
    [Fact]
    public void ToDataUrl_FormatsTheWirePayload()
    {
        var attachment = new CliImageAttachment("cat.png", "image/png", "TWFu", 800, 600, 3);

        Assert.Equal("data:image/png;base64,TWFu", attachment.ToDataUrl());
    }

    [Fact]
    public void Label_ShowsNameDimensionsAndSize()
    {
        var attachment = new CliImageAttachment("cat.png", "image/png", "TWFu", 800, 600, 120 * 1024);

        Assert.Equal("cat.png  800x600  120KB", attachment.Label);
    }

    [Theory]
    [InlineData(512, "512B")]
    [InlineData(2048, "2KB")]
    [InlineData(4 * 1024 * 1024, "4MB")]
    public void FormatBytes_UsesReadableUnits(long bytes, string expected) =>
        Assert.Equal(expected, CliImageAttachment.FormatBytes(bytes));
}
