using System.IO;
using System.IO.Compression;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AttachmentDocumentExtractorTests
{
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return output.ToArray();
    }

    [Fact]
    public void Word_documents_extract_their_paragraph_text()
    {
        var bytes = Zip(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Release notes</w:t></w:r></w:p>
                <w:p><w:r><w:t>Fixed the parser.</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));

        var text = AttachmentDocumentExtractor.Extract("notes.docx", bytes, CancellationToken.None);

        Assert.Contains("Release notes", text);
        Assert.Contains("Fixed the parser.", text);
    }

    [Fact]
    public void Presentations_extract_slide_text_in_slide_order()
    {
        var bytes = Zip(
            ("ppt/slides/slide2.xml", """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:t>Second</a:t></p:sld>"""),
            ("ppt/slides/slide1.xml", """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:t>First</a:t></p:sld>"""));

        var text = AttachmentDocumentExtractor.Extract("deck.pptx", bytes, CancellationToken.None);

        Assert.True(text.IndexOf("First", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void Workbooks_extract_shared_strings_with_sheet_headers()
    {
        var bytes = Zip(
            ("xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>Alpha</t></si>
                  <si><t>Beta</t></si>
                </sst>
                """),
            ("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row><c t="s"><v>0</v></c><c t="s"><v>1</v></c></row>
                  </sheetData>
                </worksheet>
                """));

        var text = AttachmentDocumentExtractor.Extract("book.xlsx", bytes, CancellationToken.None);

        Assert.Contains("[Sheet: sheet1]", text);
        Assert.Contains("Alpha\tBeta", text);
    }

    [Fact]
    public void Long_documents_are_truncated_with_an_explicit_marker()
    {
        // Enough lines to pass the extract bound (half the run prompt bound).
        var lines = AttachmentDocumentExtractor.MaxExtractedChars / "line of text".Length + 1_000;
        var body = string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>line of text</w:t></w:r></w:p>", lines));
        var bytes = Zip(("word/document.xml", $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>{body}</w:body>
            </w:document>
            """));

        var text = AttachmentDocumentExtractor.Extract("long.docx", bytes, CancellationToken.None);

        Assert.True(text.IndexOf("[Extracted text truncated", StringComparison.Ordinal)
            >= AttachmentDocumentExtractor.MaxExtractedChars);
        Assert.Contains("The document is longer.", text);
    }

    [Fact]
    public void Documents_without_text_have_a_named_failure()
    {
        var bytes = Zip(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>
            """));

        var error = Assert.Throws<AttachmentExtractionException>(
            () => AttachmentDocumentExtractor.Extract("empty.docx", bytes, CancellationToken.None));
        Assert.Equal("document_no_text", error.Code);
    }

    [Fact]
    public void Unsupported_documents_are_named()
    {
        Assert.False(AttachmentDocumentExtractor.IsSupportedDocument("archive.zip"));
        Assert.True(AttachmentDocumentExtractor.IsSupportedDocument("paper.PDF"));
        Assert.True(AttachmentDocumentExtractor.IsSupportedDocument("report.docx"));
        Assert.True(AttachmentDocumentExtractor.IsSupportedDocument("book.xlsx"));
        Assert.True(AttachmentDocumentExtractor.IsSupportedDocument("deck.pptx"));
    }
}
