using System.Drawing;
using System.Drawing.Imaging;
using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests.Images;

public sealed class CliImageFilesTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "ru-img-tests-" + Guid.NewGuid().ToString("N"));

    public CliImageFilesTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private string WritePng(string name, int width = 32, int height = 24)
    {
        var path = Path.Combine(_workspace, name);
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.RebeccaPurple);
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public async Task LoadAsync_RelativePathResolvesAgainstTheWorkspace()
    {
        WritePng("cat.png");

        var (attachment, error) = await CliImageFiles.LoadAsync("cat.png", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Equal("cat.png", attachment!.FileName);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(32, attachment.Width);
        Assert.Equal(24, attachment.Height);
        Assert.StartsWith("data:image/png;base64,", attachment.ToDataUrl(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_TrimsQuotesAroundThePath()
    {
        WritePng("cat.png");

        var (attachment, error) = await CliImageFiles.LoadAsync("\"cat.png\"", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attachment);
    }

    [Fact]
    public async Task LoadAsync_MissingFileFailsWithBoundedReason()
    {
        var (attachment, error) = await CliImageFiles.LoadAsync("nope.png", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("image_not_found:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_NonImageExtensionFailsWithBoundedReason()
    {
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "hello");

        var (attachment, error) = await CliImageFiles.LoadAsync("notes.txt", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("unsupported_image:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_UnreadableContentFailsWithBoundedReason()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "fake.png"), new byte[64]);

        var (attachment, error) = await CliImageFiles.LoadAsync("fake.png", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("invalid_image:", error, StringComparison.Ordinal);
    }
}
