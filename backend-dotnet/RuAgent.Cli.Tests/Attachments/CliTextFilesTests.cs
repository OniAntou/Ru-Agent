using System.Text;
using RuAgent.Cli.Attachments;
using Xunit;

namespace RuAgent.Cli.Tests.Attachments;

public sealed class CliTextFilesTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "ru-txt-tests-" + Guid.NewGuid().ToString("N"));

    public CliTextFilesTests() => Directory.CreateDirectory(_workspace);

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

    [Fact]
    public async Task LoadAsync_PreCancelledRequestDoesNotLoadAnExistingTextFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "cancel.md"), "pending text");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliTextFiles.LoadAsync("cancel.md", _workspace, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public async Task LoadAsync_ReadsATextFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.md"), "# Hello\nbody");

        var (attachment, error) = await CliTextFiles.LoadAsync("notes.md", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Equal("notes.md", attachment!.FileName);
        Assert.Equal("md", attachment.Extension);
        Assert.Contains("# Hello", attachment.TextContent);
    }

    [Fact]
    public async Task LoadAsync_StripsTheUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Hello")).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "bom.md"), bytes);

        var (attachment, error) = await CliTextFiles.LoadAsync("bom.md", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Equal("# Hello", attachment!.TextContent);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("deck.PPTX")]
    [InlineData("book.xlsx")]
    [InlineData("archive.zip")]
    [InlineData("tool.exe")]
    public async Task Any_workspace_file_attaches_as_a_path_the_model_reads(string name)
    {
        // ADR 0053: attaching never needs a type the client understands.
        await File.WriteAllBytesAsync(Path.Combine(_workspace, name), [0x50, 0x4B, 0x03, 0x04]);

        var (attachment, error) = await CliTextFiles.LoadAsync(name, _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(name, attachment!.ReferencePath);
        Assert.False(attachment.IsStaged);
    }

    [Fact]
    public async Task LoadAsync_MissingFileFailsWithBoundedReason()
    {
        var (attachment, error) = await CliTextFiles.LoadAsync("nope.md", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("attachment_not_found:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_is_refused_by_name()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));

        var (attachment, error) = await CliTextFiles.LoadAsync("src", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("attachment_is_folder:", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("spec.pdf")]
    [InlineData("report.docx")]
    [InlineData("big.txt")]
    public async Task A_file_outside_the_workspace_is_copied_in_and_attached_by_path(string name)
    {
        var outsideFolder = _workspace + "-elsewhere";
        Directory.CreateDirectory(outsideFolder);
        try
        {
            var source = Path.Combine(outsideFolder, name);
            var bytes = name == "big.txt"
                ? Encoding.UTF8.GetBytes(new string('x', (int)CliTextFiles.ReferenceThresholdBytes + 1))
                : "%PDF-1.4 MARKER"u8.ToArray();
            await File.WriteAllBytesAsync(source, bytes);

            var (attachment, error) = await CliTextFiles.LoadAsync(source, _workspace, CancellationToken.None);

            Assert.Null(error);
            Assert.True(attachment!.IsStaged);
            Assert.Equal($".ru/attachments/{name}", attachment.ReferencePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_workspace, ".ru", "attachments", name)));
            Assert.Contains("(copied to .ru/attachments/", attachment.Label, StringComparison.Ordinal);
            var prompt = AttachedPrompt.Compose("read it", [attachment]);
            Assert.StartsWith($"[Attached workspace file: .ru/attachments/{name} (", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outsideFolder, recursive: true);
        }
    }

    [Fact]
    public async Task Small_text_outside_the_workspace_is_still_inlined()
    {
        var outsideFolder = _workspace + "-elsewhere";
        Directory.CreateDirectory(outsideFolder);
        try
        {
            var source = Path.Combine(outsideFolder, "note.md");
            await File.WriteAllTextAsync(source, "MARKER-INLINE");

            var (attachment, error) = await CliTextFiles.LoadAsync(source, _workspace, CancellationToken.None);

            Assert.Null(error);
            Assert.False(attachment!.IsReference);
            Assert.Equal("MARKER-INLINE", attachment.TextContent);
            Assert.False(Directory.Exists(Path.Combine(_workspace, ".ru")));
        }
        finally
        {
            Directory.Delete(outsideFolder, recursive: true);
        }
    }

    [Fact]
    public async Task A_large_workspace_file_is_sent_as_a_path_the_model_reads()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "src", "big.cs"), new string('x', (int)CliTextFiles.ReferenceThresholdBytes + 1));

        var (attachment, error) = await CliTextFiles.LoadAsync(Path.Combine("src", "big.cs"), _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.True(attachment!.IsReference);
        Assert.Equal("src/big.cs", attachment.ReferencePath);
        Assert.Empty(attachment.TextContent);
        var prompt = RuAgent.Cli.Attachments.AttachedPrompt.Compose("review it", [attachment]);
        Assert.StartsWith("[Attached workspace file: src/big.cs (", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binary_file_named_as_text_is_sent_by_path_instead_of_inlined()
    {
        // Not UTF-8: the model gets the path, and read says what the bytes are.
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "data.txt"), [0x48, 0x00, 0x49, 0xFF]);

        var (attachment, error) = await CliTextFiles.LoadAsync("data.txt", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("data.txt", attachment!.ReferencePath);
        Assert.Empty(attachment.TextContent);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("deck.PPTX")]
    [InlineData("book.xlsx")]
    [InlineData("archive.zip")]
    public void LoadBytes_RejectsUnsupportedAttachments(string name)
    {
        var (attachment, error) = CliTextFiles.LoadBytes(name, [0x50, 0x4B, 0x03, 0x04], 4);

        Assert.Null(attachment);
        Assert.StartsWith("unsupported_attachment:", error, StringComparison.Ordinal);
    }
}
