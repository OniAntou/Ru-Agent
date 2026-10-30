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

    [Fact]
    public async Task LoadAsync_PreCancelledRequestDoesNotLoadAnExistingImage()
    {
        var path = WritePng("cancel.png");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliImageFiles.LoadAsync(path, _workspace, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public void CancelledByteProcessingDoesNotBecomeAnInvalidImageError()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CliImageFiles.LoadBytes("invalid.png", [1, 2, 3], cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CliImageResizer.ResizeIfNeeded([1, 2, 3], cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task LoadStream_CancelsThePendingReadWithTheCallersToken()
    {
        using var cancelled = new CancellationTokenSource();
        using var stream = new WaitingStream();
        var loading = CliImageFiles.LoadStreamAsync("cancel.png", stream, cancelled.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal(cancelled.Token, stream.ReadToken);
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public async Task LoadStream_ObservesCancellationEvenWhenTheStreamReturnsEof()
    {
        using var cancelled = new CancellationTokenSource();
        using var stream = new CancellingEofStream(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliImageFiles.LoadStreamAsync("cancel.png", stream, cancelled.Token));
    }

    private sealed class WaitingStream : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReadToken;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CancellingEofStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(0);
        }
    }
}
