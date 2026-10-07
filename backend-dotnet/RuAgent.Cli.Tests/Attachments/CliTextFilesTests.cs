using System.IO.Compression;
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

    [Fact]
    public async Task LoadAsync_ExtractsOfficeDocumentText()
    {
        var docx = Docx("<w:document xmlns:w=\"x\"><w:body><w:p><w:t>Quarterly report</w:t></w:p></w:body></w:document>");
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "report.docx"), docx);

        var (attachment, error) = await CliTextFiles.LoadAsync("report.docx", _workspace, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Contains("Quarterly report", attachment!.TextContent);
    }

    [Fact]
    public async Task LoadAsync_MissingFileFailsWithBoundedReason()
    {
        var (attachment, error) = await CliTextFiles.LoadAsync("nope.md", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("attachment_not_found:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_UnsupportedExtensionFailsWithBoundedReason()
    {
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "archive.zip"), [1, 2, 3]);

        var (attachment, error) = await CliTextFiles.LoadAsync("archive.zip", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("unsupported_attachment:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_OversizedTextFailsWithBoundedReason()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_workspace, "big.txt"),
            new byte[RuAgent.Cli.Images.CliImagePolicy.MaxTextAttachmentBytes + 1]);

        var (attachment, error) = await CliTextFiles.LoadAsync("big.txt", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("attachment_too_large:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MalformedOfficeFailsWithBoundedReason()
    {
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "broken.docx"), new byte[256]);

        var (attachment, error) = await CliTextFiles.LoadAsync("broken.docx", _workspace, CancellationToken.None);

        Assert.Null(attachment);
        Assert.StartsWith("invalid_document:", error, StringComparison.Ordinal);
    }

    private static byte[] Docx(string documentXml)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(documentXml);
        }

        return stream.ToArray();
    }
}
