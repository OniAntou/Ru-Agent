using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// <c>read</c> content coverage beyond plain UTF-8: PDFs are extracted with a page range,
/// and other binary files fail by name with a hint naming the command that opens them.
/// Office and OpenDocument extraction is covered by <see cref="FileReadOfficeTests"/>.
/// </summary>
public sealed class FileReadDocumentTests : IDisposable
{
    private readonly string _root;
    private readonly FileReadAdapter _adapter;

    public FileReadDocumentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-read-document-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _adapter = new FileReadAdapter(_root);
    }

    [Fact]
    public async Task Pdf_returns_extracted_text_and_page_metadata()
    {
        File.WriteAllBytes(FullPath("paper.pdf"), Pdf("First page text", "Second page text"));

        var result = await _adapter.ExecuteAsync(Args("paper.pdf"), imageInputAccepted: true, CancellationToken.None);

        Assert.Null(result.Image);
        Assert.Equal("pdf", result.Output["kind"]!.GetValue<string>());
        Assert.Equal(2, result.Output["page_count"]!.GetValue<int>());
        Assert.Equal("1-2", result.Output["pages"]!.GetValue<string>());
        var content = result.Output["content"]!.GetValue<string>();
        Assert.Contains("First page text", content, StringComparison.Ordinal);
        Assert.Contains("Second page text", content, StringComparison.Ordinal);
        Assert.False(result.Output["truncated"]!.GetValue<bool>());
        Assert.Equal(content.Length, result.Output["chars_read"]!.GetValue<int>());
        Assert.Equal((content.Length + 3) / 4, result.Output["estimated_tokens"]!.GetValue<int>());
        Assert.Equal(64, result.Output["sha256"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task A_long_pdf_reads_ten_pages_by_default_and_names_the_next_range()
    {
        File.WriteAllBytes(
            FullPath("long.pdf"),
            Pdf(Enumerable.Range(1, 12).Select(index => $"Page text {index}").ToArray()));

        var result = await _adapter.ExecuteAsync(Args("long.pdf"), imageInputAccepted: true, CancellationToken.None);

        Assert.Equal("1-10", result.Output["pages"]!.GetValue<string>());
        var content = result.Output["content"]!.GetValue<string>();
        Assert.Contains("Page text 1", content, StringComparison.Ordinal);
        Assert.Contains("Page text 10", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Page text 11", content, StringComparison.Ordinal);
        Assert.True(result.Output["truncated"]!.GetValue<bool>());
        Assert.Contains("pages=\"11-12\"", result.Output["notice"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_page_range_is_read_verbatim()
    {
        File.WriteAllBytes(
            FullPath("range.pdf"),
            Pdf(Enumerable.Range(1, 12).Select(index => $"Page text {index}").ToArray()));
        var arguments = Args("range.pdf");
        arguments["pages"] = "11-12";

        var result = await _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None);

        Assert.Equal("11-12", result.Output["pages"]!.GetValue<string>());
        var content = result.Output["content"]!.GetValue<string>();
        Assert.Contains("Page text 11", content, StringComparison.Ordinal);
        Assert.Contains("Page text 12", content, StringComparison.Ordinal);
        Assert.False(result.Output["truncated"]!.GetValue<bool>());
        Assert.False(result.Output.ContainsKey("notice"));
    }

    [Theory]
    [InlineData("0", "pdf_pages_out_of_range")]
    [InlineData("13-14", "pdf_pages_out_of_range")]
    [InlineData("abc", "invalid_pdf_pages")]
    [InlineData("3-2", "invalid_pdf_pages")]
    public async Task A_bad_pdf_page_range_fails_by_name(string pages, string code)
    {
        File.WriteAllBytes(
            FullPath("bad-range.pdf"),
            Pdf(Enumerable.Range(1, 12).Select(index => $"Page text {index}").ToArray()));
        var arguments = Args("bad-range.pdf");
        arguments["pages"] = pages;

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public async Task A_pdf_range_over_twenty_pages_fails_with_a_paging_hint()
    {
        File.WriteAllBytes(
            FullPath("wide.pdf"),
            Pdf(Enumerable.Range(1, 30).Select(index => $"Page text {index}").ToArray()));
        var arguments = Args("wide.pdf");
        arguments["pages"] = "1-25";

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("pdf_pages_too_large", error.Code);
        Assert.Contains("20", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_offset_fails_with_a_page_range_hint()
    {
        File.WriteAllBytes(FullPath("paper.pdf"), Pdf("First page text"));
        var arguments = Args("paper.pdf");
        arguments["offset"] = 4;

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("pdf_offset_unsupported", error.Code);
        Assert.Contains("pages=", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_pdf_fails_by_name()
    {
        File.WriteAllBytes(FullPath("broken.pdf"), "%PDF-1.7 not really a document"u8.ToArray());

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteAsync(Args("broken.pdf"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("pdf_unreadable", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public async Task A_scanned_pdf_reports_that_no_text_was_extractable()
    {
        File.WriteAllBytes(FullPath("scan.pdf"), Pdf(string.Empty));

        var result = await _adapter.ExecuteAsync(Args("scan.pdf"), imageInputAccepted: true, CancellationToken.None);

        Assert.Equal(string.Empty, result.Output["content"]!.GetValue<string>());
        Assert.Contains("No extractable text", result.Output["notice"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binary_file_fails_with_a_binary_hint()
    {
        File.WriteAllBytes(FullPath("payload.bin"), TestImages.Png(10, 10));

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteAsync(Args("payload.bin"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("binary_file_unsupported", error.Code);
        Assert.Contains("binary", error.Hint, StringComparison.Ordinal);
        Assert.Contains("Format-Hex <path>", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_utf8_without_binary_bytes_keeps_the_text_repair_path()
    {
        File.WriteAllBytes(FullPath("legacy.txt"), [0x61, 0xC3]);

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _adapter.ExecuteAsync(Args("legacy.txt"), imageInputAccepted: true, CancellationToken.None));

        Assert.Equal("invalid_utf8", error.Message);
        Assert.Contains("legacy code page", FileReadFailureHints.Of(error), StringComparison.Ordinal);
    }

    private static byte[] Pdf(params string[] pageTexts)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (!string.IsNullOrEmpty(text))
            {
                page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(72, 720), font);
            }
        }

        return builder.Build();
    }

    private string FullPath(string name) => Path.Combine(_root, name);

    private static JsonObject Args(string name) => new() { ["path"] = name };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
