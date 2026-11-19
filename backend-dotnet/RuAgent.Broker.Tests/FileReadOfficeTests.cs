using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// ADR 0053: <c>read</c> extracts Office Open XML and OpenDocument text by part range, and a
/// read it cannot serve names the next step (the command, or how to attach the file) instead
/// of failing as bare "invalid utf8" or "access denied".
/// </summary>
public sealed class FileReadOfficeTests : IDisposable
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _root;
    private readonly FileReadAdapter _adapter;

    public FileReadOfficeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-read-office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _adapter = new FileReadAdapter(_root);
    }

    [Fact]
    public async Task Docx_returns_paragraph_text_paged_by_part()
    {
        File.WriteAllBytes(FullPath("report.docx"), Docx("First paragraph MARKER-DOCX-1", "", "Second paragraph", "Third paragraph"));

        var result = await ReadAsync("report.docx");

        Assert.Equal("document", result.Output["kind"]!.GetValue<string>());
        Assert.Equal("docx", result.Output["format"]!.GetValue<string>());
        Assert.Equal("paragraph", result.Output["unit"]!.GetValue<string>());
        Assert.Equal(3, result.Output["part_count"]!.GetValue<int>());
        Assert.Equal("1-3", result.Output["pages"]!.GetValue<string>());
        Assert.Equal("First paragraph MARKER-DOCX-1\nSecond paragraph\nThird paragraph", Content(result));
        Assert.False(result.Output["truncated"]!.GetValue<bool>());
        Assert.Null(result.Output["offset"]);

        var tail = await ReadAsync("report.docx", "2-3");
        Assert.Equal("Second paragraph\nThird paragraph", Content(tail));
    }

    [Fact]
    public async Task Xlsx_returns_rows_with_sheet_names_and_shared_strings()
    {
        File.WriteAllBytes(FullPath("book.xlsx"), Zip(
            ("xl/workbook.xml", $"<workbook xmlns=\"{SpreadsheetNs}\"><sheets><sheet name=\"Budget\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{SpreadsheetNs}\"><si><t>Item</t></si><si><t>MARKER-XLSX-2</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{SpreadsheetNs}\"><sheetData>"
                + "<row><c t=\"s\"><v>0</v></c><c><v>42</v></c></row>"
                + "<row><c t=\"s\"><v>1</v></c><c t=\"inlineStr\"><is><t>inline</t></is></c></row>"
                + "</sheetData></worksheet>")));

        var result = await ReadAsync("book.xlsx");

        Assert.Equal("row", result.Output["unit"]!.GetValue<string>());
        Assert.Equal(2, result.Output["part_count"]!.GetValue<int>());
        Assert.Equal("[Sheet: Budget]\nItem\t42\nMARKER-XLSX-2\tinline", Content(result));
    }

    [Fact]
    public async Task Xlsx_follows_workbook_order_and_keeps_sparse_columns_aligned()
    {
        // sheet2.xml is the first sheet in the workbook; a row with only A and D keeps D in column 4.
        File.WriteAllBytes(FullPath("ordered.xlsx"), Zip(
            ("xl/workbook.xml", $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Summary\" r:id=\"rId2\"/><sheet name=\"Data\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{SpreadsheetNs}\"><sheetData><row><c r=\"A1\"><v>1</v></c><c r=\"D1\"><v>4</v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{SpreadsheetNs}\"><sheetData><row><c r=\"B1\"><v>9</v></c></row></sheetData></worksheet>")));

        var result = await ReadAsync("ordered.xlsx");

        Assert.Equal("[Sheet: Summary]\n\t9\n[Sheet: Data]\n1\t\t\t4", Content(result));
    }

    [Fact]
    public async Task Pptx_returns_slides_in_numeric_order()
    {
        File.WriteAllBytes(FullPath("deck.pptx"), Zip(
            ("ppt/slides/slide10.xml", Slide("Tenth")),
            ("ppt/slides/slide2.xml", Slide("Second MARKER-PPTX-3")),
            ("ppt/slides/slide1.xml", Slide("First"))));

        var result = await ReadAsync("deck.pptx");

        Assert.Equal("slide", result.Output["unit"]!.GetValue<string>());
        Assert.Equal("[Slide 1]\nFirst\n[Slide 2]\nSecond MARKER-PPTX-3\n[Slide 10]\nTenth", Content(result));
    }

    [Theory]
    [InlineData("notes.odt", "<office:text><text:h>Title</text:h><text:p>MARKER-ODF-4</text:p></office:text>", "Title\nMARKER-ODF-4")]
    [InlineData("sheet.ods", "<office:spreadsheet><table:table table:name=\"Data\"><table:table-row><table:table-cell><text:p>MARKER-ODF-4</text:p></table:table-cell><table:table-cell><text:p>7</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet>", "[Sheet: Data]\nMARKER-ODF-4\t7")]
    [InlineData("talk.odp", "<office:presentation><draw:page><draw:frame><draw:text-box><text:p>MARKER-ODF-4</text:p></draw:text-box></draw:frame></draw:page></office:presentation>", "[Slide 1]\nMARKER-ODF-4")]
    public async Task OpenDocument_files_return_their_text(string name, string body, string expected)
    {
        File.WriteAllBytes(FullPath(name), Zip(("content.xml",
            "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
            + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" "
            + "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
            + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\">"
            + $"<office:body>{body}</office:body></office:document-content>")));

        var result = await ReadAsync(name);

        Assert.Equal(expected, Content(result));
    }

    [Fact]
    public async Task A_long_document_names_the_next_part_range()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(index => $"P{index} " + new string('w', 3_000)).ToArray();
        File.WriteAllBytes(FullPath("long.docx"), Docx(paragraphs));

        var result = await ReadAsync("long.docx");

        Assert.True(result.Output["truncated"]!.GetValue<bool>());
        Assert.Equal(FileOfficeTextExtractor.MaxCharacters, Content(result).Length);
        Assert.Contains("pages=\"20-40\"", result.Output["notice"]!.GetValue<string>(), StringComparison.Ordinal);

        var next = await ReadAsync("long.docx", "20-40");
        Assert.StartsWith("P20 ", Content(next), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_range_past_the_end_fails_by_name()
    {
        File.WriteAllBytes(FullPath("short.docx"), Docx("only"));

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(() => ReadAsync("short.docx", "5"));

        Assert.Equal("document_pages_out_of_range", error.Code);
    }

    [Fact]
    public async Task A_damaged_docx_fails_as_unreadable_with_a_next_step()
    {
        File.WriteAllBytes(FullPath("broken.docx"), [0x50, 0x4B, 0x03, 0x04, 0x00]);

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(() => ReadAsync("broken.docx"));

        Assert.Equal("document_unreadable", error.Code);
        Assert.Contains("PDF or text export", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_protected_docx_fails_as_encrypted()
    {
        File.WriteAllBytes(FullPath("locked.docx"), [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(() => ReadAsync("locked.docx"));

        Assert.Equal("document_encrypted", error.Code);
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("old.xls")]
    [InlineData("deck.key")]
    public async Task Legacy_documents_fail_with_a_conversion_command(string name)
    {
        File.WriteAllBytes(FullPath(name), [0xD0, 0xCF, 0x11, 0xE0, 0x00]);

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(() => ReadAsync(name));

        Assert.Equal("document_unsupported", error.Code);
        Assert.Contains(Path.GetExtension(name), error.Hint, StringComparison.Ordinal);
        Assert.Contains("soffice --headless --convert-to", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rtf_reads_as_text()
    {
        File.WriteAllText(FullPath("memo.rtf"), @"{\rtf1\ansi MARKER-RTF}");

        var result = await ReadAsync("memo.rtf");

        Assert.Contains("MARKER-RTF", Content(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zip_archive_names_the_command_that_lists_it()
    {
        File.WriteAllBytes(FullPath("bundle.zip"), Zip(("inner.txt", "hello")));

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(() => ReadAsync("bundle.zip"));

        Assert.Equal("binary_file_unsupported", error.Code);
        Assert.Contains("ZIP archive", error.Hint, StringComparison.Ordinal);
        Assert.Contains("Expand-Archive <path>", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Utf16_text_names_the_decoding_command()
    {
        File.WriteAllBytes(FullPath("wide.txt"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello")]);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync("wide.txt"));

        // UTF-16 holds NUL bytes, so it lands on the binary branch; either way the hint
        // must name the encoding rather than call it opaque binary.
        var hint = error is FileReadRefusalException refusal ? refusal.Hint : FileReadFailureHints.Of(error);
        Assert.Contains("UTF-16", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_outside_the_workspace_says_how_to_attach_it()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-read-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "x");
        try
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadAsync(outside));

            Assert.Equal("outside_workspace", error.Message);
            Assert.Contains(".ru/attachments", FileReadFailureHints.Of(error), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_line_read_of_a_document_points_at_read()
    {
        File.WriteAllBytes(FullPath("report.docx"), Docx("x"));

        var error = await Assert.ThrowsAsync<FileReadDocumentException>(async () =>
            await _adapter.ExecuteLinesAsync(new JsonObject { ["path"] = "report.docx" }, CancellationToken.None));

        Assert.Equal("read_file_text_only", error.Code);
    }

    [Fact]
    public async Task A_staged_attachment_under_dot_ru_is_readable_even_when_named_like_ru_config()
    {
        // The composer stages outside files in .ru/attachments; only .ru/hooks.json and
        // .ru/mcp.json directly under .ru are protected, so a staged copy stays readable.
        Directory.CreateDirectory(FullPath(Path.Combine(".ru", "attachments")));
        File.WriteAllText(FullPath(Path.Combine(".ru", "attachments", "hooks.json")), "{\"marker\":\"staged\"}");

        var result = await ReadAsync(".ru/attachments/hooks.json");

        Assert.Contains("staged", Content(result), StringComparison.Ordinal);
    }

    private Task<FileReadResult> ReadAsync(string path, string? pages = null)
    {
        var arguments = new JsonObject { ["path"] = path };
        if (pages is not null) arguments["pages"] = pages;
        return _adapter.ExecuteAsync(arguments, imageInputAccepted: true, CancellationToken.None).AsTask();
    }

    private static string Content(FileReadResult result) => result.Output["content"]!.GetValue<string>();

    private string FullPath(string name) => Path.Combine(_root, name);

    private static byte[] Docx(params string[] paragraphs) => Zip(("word/document.xml",
        "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
        + string.Concat(paragraphs.Select(text => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>"))
        + "</w:body></w:document>"));

    private static string Slide(string text) =>
        "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
        + $"<p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";

    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

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
